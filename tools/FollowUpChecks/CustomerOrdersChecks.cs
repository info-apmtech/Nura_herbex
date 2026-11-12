using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Controllers;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

/// <summary>
/// Offline verification for Admin manual customer creation (<see cref="CustomerAuthService.CreateByAdminAsync"/>),
/// creating COD / Already-Paid orders directly for a <see cref="Customer"/> with exact <see cref="Order.CustomerId"/> mapping,
/// strict cross-customer isolation, backfill rules, background Email/WhatsApp confirmations,
/// <see cref="StaffController.LeadOrder"/> CustomerId resolution, and <see cref="PageRules"/> rights.
/// </summary>
public static class CustomerOrdersChecks
{
    private sealed class CapturingHandler(ConcurrentQueue<(string Url, string Body)> captured, string okJson) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            captured.Enqueue((request.RequestUri?.ToString() ?? "", body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(okJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static ClaimsPrincipal SuperAdminPrincipal(string email, string name = "Super Admin") =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Role, EmployeeRoles.Admin),
        ], "Test"));

    private static async Task PrepareStaffControllerAsync(StaffControllerBase controller, ClaimsPrincipal user, IServiceProvider sp, string path)
    {
        var httpContext = new DefaultHttpContext { User = user, RequestServices = sp };
        httpContext.Request.Path = "/api/admin/presence/offline";
        var actionContext = new ActionContext(httpContext, new RouteData(), new ControllerActionDescriptor());
        controller.ControllerContext = new ControllerContext(actionContext);
        var execContext = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller);
        await controller.OnActionExecutionAsync(execContext, () =>
        {
            httpContext.Request.Path = path;
            return Task.FromResult(new ActionExecutedContext(actionContext, [], controller));
        });
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 2500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        // ---- 1) PageRules & RightsService matrix -------------------------------------------------
        var createCustRule = PageRules.Match("/api/admin/customers", "POST");
        var listCustRule = PageRules.Match("/api/admin/customers", "GET");
        var orderRule = PageRules.Match("/api/admin/customers/CUST-123456/order", "POST");
        var nestedBadOrderRule = PageRules.Match("/api/admin/customers/CUST-123456/order/extra", "POST");

        check(createCustRule.Kind == PageRules.Kind.Pages
              && createCustRule.Pages.SequenceEqual(["customers"])
              && createCustRule.MinLevel == PageCatalog.Full
              && listCustRule.MinLevel == PageCatalog.Limited,
            "CustOrder: PageRules requires Full customers access for POST /api/admin/customers and Limited for GET");

        check(orderRule.Kind == PageRules.Kind.Pages
              && orderRule.Pages.SequenceEqual(["customers", "orders"])
              && orderRule.MinLevel == PageCatalog.Full
              && nestedBadOrderRule.Pages.SequenceEqual(["customers"]),
            "CustOrder: PageRules matches POST /api/admin/customers/{id}/order with ['customers', 'orders'] at Full");

        // Verify resolved designation levels against PageRules.MinLevel
        var rightsDbOpts = new DbContextOptionsBuilder<NuraDbContext>().UseInMemoryDatabase("cust-rights-" + Guid.NewGuid().ToString("N")).Options;
        await using (var rdb = new NuraDbContext(rightsDbOpts))
        {
            var dCustFull = new Designation { Name = "Cust Full", Level = 2, RightsJson = PageCatalog.ToJson(new Dictionary<string, int> { ["customers"] = 2 }) };
            var dOrdersFull = new Designation { Name = "Orders Full", Level = 2, RightsJson = PageCatalog.ToJson(new Dictionary<string, int> { ["customers"] = 1, ["orders"] = 2 }) };
            var dReadOnly = new Designation { Name = "Read Only", Level = 2, RightsJson = PageCatalog.ToJson(new Dictionary<string, int> { ["customers"] = 1, ["orders"] = 1 }) };
            rdb.Designations.AddRange(dCustFull, dOrdersFull, dReadOnly);
            await rdb.SaveChangesAsync();
            var eCustFull = new Employee { Name = "E1", Email = "e1@x.test", Role = EmployeeRole.Sales, IsActive = true, DesignationId = dCustFull.Id };
            var eOrdersFull = new Employee { Name = "E2", Email = "e2@x.test", Role = EmployeeRole.Sales, IsActive = true, DesignationId = dOrdersFull.Id };
            var eReadOnly = new Employee { Name = "E3", Email = "e3@x.test", Role = EmployeeRole.Sales, IsActive = true, DesignationId = dReadOnly.Id };
            rdb.Employees.AddRange(eCustFull, eOrdersFull, eReadOnly);
            await rdb.SaveChangesAsync();

            using var rightsCache = new MemoryCache(new MemoryCacheOptions());
            var rs = new RightsService(rdb, rightsCache);
            bool Allows(ResolvedRights resolved, PageRules.Rule rule) =>
                rule.Pages.Any(p => resolved.Level(p) >= rule.MinLevel);

            var rCust = await rs.ResolveEmployeeAsync(eCustFull.Id);
            var rOrd = await rs.ResolveEmployeeAsync(eOrdersFull.Id);
            var rRo = await rs.ResolveEmployeeAsync(eReadOnly.Id);
            check(Allows(rCust, createCustRule) && Allows(rCust, orderRule)
                  && !Allows(rOrd, createCustRule) && Allows(rOrd, orderRule)
                  && !Allows(rRo, createCustRule) && !Allows(rRo, orderRule),
                "CustOrder: Staff with Full customers OR Full orders can create customer orders; view-only staff are blocked");
        }

        // ---- 2) Full service + controller workflow on isolated InMemory DB -------------------------
        var sentEmails = new ConcurrentQueue<(string Url, string Body)>();
        var sentWa = new ConcurrentQueue<(string Url, string Body)>();
        using var emailHandler = new CapturingHandler(sentEmails, "{\"id\":\"email-ok\"}");
        using var waHandler = new CapturingHandler(sentWa, "{\"messages\":[{\"id\":\"wamid.CUSTORDER\"}]}");
        using var courierHandler = new CapturingHandler(new ConcurrentQueue<(string, string)>(), "{}");

        var waOptions = Options.Create(new WhatsAppOptions { AccessToken = "wa-test-token", PhoneNumberId = "1", DefaultCountryCode = "91", AutoReplyEnabled = false });
        var emailOptions = Options.Create(new EmailOptions { ResendApiKey = "resend-test-key" });
        var storeOptions = Options.Create(new StoreOptions { FrontendUrl = "https://shop.test", FreeShippingThreshold = 999, FlatShippingFee = 99 });
        var jwtOptions = Options.Create(new JwtOptions { Secret = new string('k', 40), Issuer = "nura", Audience = "nura" });
        var shipOptions = Options.Create(new ShippingOptions());
        var sfOptions = Options.Create(new ShadowfaxOptions());

        var dbName = "cust-orders-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddDbContext<NuraDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(waOptions);
        services.AddSingleton(emailOptions);
        services.AddSingleton(storeOptions);
        services.AddSingleton(jwtOptions);
        services.AddSingleton(shipOptions);
        services.AddSingleton(sfOptions);
        services.AddSingleton<ShippingSettingsStore>();
        services.AddSingleton<TokenService>();
        services.AddSingleton<MediaSigner>();
        services.AddScoped<StaffAvailability>();
        services.AddScoped<MessageTemplateService>();
        services.AddScoped<LeadService>();
        services.AddScoped<ProductService>();
        services.AddScoped<OrderOperationLock>();
        services.AddScoped<SettingsService>();
        services.AddScoped<RightsService>();
        services.AddScoped(_ => new EmailService(new HttpClient(emailHandler, disposeHandler: false), emailOptions, storeOptions, NullLogger<EmailService>.Instance));
        services.AddScoped(p => new WhatsAppService(p.GetRequiredService<NuraDbContext>(), new HttpClient(waHandler, disposeHandler: false), p.GetRequiredService<LeadService>(), waOptions, NullLogger<WhatsAppService>.Instance));
        services.AddScoped(_ => new ShiprocketService(new HttpClient(courierHandler, disposeHandler: false), Options.Create(new ShiprocketOptions()), new ShippingSettingsStore(), NullLogger<ShiprocketService>.Instance));
        services.AddScoped(_ => new ShadowfaxService(new HttpClient(courierHandler, disposeHandler: false), sfOptions, new ShippingSettingsStore(), NullLogger<ShadowfaxService>.Instance));
        services.AddScoped<ShippingGateway>();
        services.AddScoped<OrderService>();
        services.AddScoped<CustomerAuthService>();

        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NuraDbContext>();
        var custAuth = scope.ServiceProvider.GetRequiredService<CustomerAuthService>();
        var orderSvc = scope.ServiceProvider.GetRequiredService<OrderService>();
        var leadsSvc = scope.ServiceProvider.GetRequiredService<LeadService>();
        var rightsSvc = scope.ServiceProvider.GetRequiredService<RightsService>();

        db.Products.Add(new Product
        {
            Id = "stamix-1",
            Sku = "STX-1",
            Name = "Stamix Pack",
            Price = 1200m,
            StockQuantity = 20,
            WeightKg = 0.5m,
            LengthCm = 10,
            BreadthCm = 10,
            HeightCm = 10,
            Status = ProductStatus.Active,
        });
        db.Coupons.Add(new Coupon
        {
            Code = "CUST10",
            DiscountType = CouponDiscountType.Percentage,
            DiscountValue = 10m,
            MinOrderAmount = 500m,
            MaxDiscountCap = 200m,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var adminCtrl = new AdminController(
            db,
            orderSvc,
            custAuth,
            scope.ServiceProvider.GetRequiredService<SettingsService>(),
            scope.ServiceProvider.GetRequiredService<TokenService>(),
            scope.ServiceProvider.GetRequiredService<EmailService>(),
            scope.ServiceProvider.GetRequiredService<WhatsAppService>(),
            new FixedMonitor<AdminOptions>(new AdminOptions()),
            waOptions,
            rightsSvc,
            scope.ServiceProvider.GetRequiredService<MediaSigner>(),
            NullLogger<AdminController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = SuperAdminPrincipal("admin@nuraherbex.com", "Admin Owner"),
                    RequestServices = scope.ServiceProvider,
                },
            },
        };

        // 2a) Validation failures on CreateCustomer
        var badNoName = await adminCtrl.CreateCustomer(new AdminCreateCustomerRequest { FullName = "   ", Phone = "9876543210" }) as ObjectResult;
        var badPhone = await adminCtrl.CreateCustomer(new AdminCreateCustomerRequest { FullName = "Test", Phone = "12345" }) as ObjectResult;
        var badEmail = await adminCtrl.CreateCustomer(new AdminCreateCustomerRequest { FullName = "Test", Phone = "9876543210", Email = "not-an-email" }) as ObjectResult;
        var badPartialAddr = await adminCtrl.CreateCustomer(new AdminCreateCustomerRequest
        {
            FullName = "Test",
            Phone = "9876543210",
            ShippingAddress = new AddressDto { City = "Chennai", Pincode = "600001" },
        }) as ObjectResult;
        var badPin = await adminCtrl.CreateCustomer(new AdminCreateCustomerRequest
        {
            FullName = "Test",
            Phone = "9876543210",
            ShippingAddress = new AddressDto { AddressLine1 = "12 Anna Salai", City = "Chennai", State = "Tamil Nadu", Pincode = "000001" },
        }) as ObjectResult;
        check(badNoName?.StatusCode == 400
              && badPhone?.StatusCode == 400
              && badEmail?.StatusCode == 400
              && badPartialAddr?.StatusCode == 400
              && badPin?.StatusCode == 400,
            "CustOrder: CreateCustomer rejects missing name, invalid phone, invalid email, partial address, and invalid pincode with 400");

        // 2b) Name + Phone ONLY (no email, no address) succeeds + canonicalizes Indian phone + hashes password
        var resA = await adminCtrl.CreateCustomer(new AdminCreateCustomerRequest
        {
            FullName = "Arun Manual",
            Phone = "+91 98401 23456",
        }) as ObjectResult;
        var dtoA = (resA?.Value as CustomerResponse)?.Customer;
        var rowA = dtoA is null ? null : await db.Customers.FirstAsync(c => c.Id == dtoA.Id);
        check(resA?.StatusCode == 201
              && dtoA is not null
              && Regex.IsMatch(dtoA.Id, @"^CUST-\d{6}$")
              && dtoA.Phone == "9840123456"
              && dtoA.Email == ""
              && string.IsNullOrEmpty(dtoA.ShippingAddress.AddressLine1)
              && rowA is not null
              && !string.IsNullOrEmpty(rowA.PasswordHash)
              && !BCrypt.Net.BCrypt.Verify("wrong-password", rowA.PasswordHash),
            "CustOrder: CreateCustomer saves Name+Phone only customer with CUST-######, canonical phone, blank email/address, and safe BCrypt hash");

        // 2c) Multiple customers with blank Email succeed; duplicate phone variants & email return 409
        var resB = await adminCtrl.CreateCustomer(new AdminCreateCustomerRequest
        {
            FullName = "Bala BlankEmail",
            Phone = "09840123457",
        }) as ObjectResult;
        var dtoB = (resB?.Value as CustomerResponse)?.Customer;
        var dupPhonePlus91 = await adminCtrl.CreateCustomer(new AdminCreateCustomerRequest { FullName = "Dup1", Phone = "919840123456" }) as ObjectResult;
        var dupPhoneZero = await adminCtrl.CreateCustomer(new AdminCreateCustomerRequest { FullName = "Dup2", Phone = "09840123456" }) as ObjectResult;
        check(resB?.StatusCode == 201
              && dtoB is not null
              && dtoB.Phone == "9840123457"
              && dupPhonePlus91?.StatusCode == 409
              && dupPhoneZero?.StatusCode == 409,
            "CustOrder: Multiple blank-email customers succeed while duplicate phone variants (+91/0/10d) return 409");

        // 2d) Full optional Email + Address + International 12-digit phone
        var resC = await adminCtrl.CreateCustomer(new AdminCreateCustomerRequest
        {
            FullName = "Chitra Complete",
            Phone = "447911123456",
            Email = "Chitra@Example.Test ",
            ShippingAddress = new AddressDto
            {
                AddressLine1 = "45 Mount Road",
                AddressLine2 = "Near Clock Tower",
                City = "Chennai",
                State = "Tamil Nadu",
                Pincode = "600002",
            },
        }) as ObjectResult;
        var dupEmail = await adminCtrl.CreateCustomer(new AdminCreateCustomerRequest
        {
            FullName = "Chitra Dup",
            Phone = "9840123499",
            Email = "chitra@example.test",
        }) as ObjectResult;
        var dtoC = (resC?.Value as CustomerResponse)?.Customer;
        check(resC?.StatusCode == 201
              && dtoC is { Phone: "447911123456", Email: "chitra@example.test" }
              && dtoC.ShippingAddress.Pincode == "600002"
              && dupEmail?.StatusCode == 409,
            "CustOrder: Optional Email+Address & 12-digit international phone persist cleanly and duplicate email returns 409");

        // 2e) Order creation guards on CreateCustomerOrder
        var shipAddr = new AddressDto { AddressLine1 = "12 Gandhi St", City = "Chennai", State = "Tamil Nadu", Pincode = "600001" };
        var missingCustOrd = await adminCtrl.CreateCustomerOrder("CUST-000000", new LeadOrderRequest
        {
            Items = [new LeadOrderLineRequest { ProductId = "stamix-1", Quantity = 1 }],
            Address = shipAddr,
        }, leadsSvc);
        var noItemsOrd = await adminCtrl.CreateCustomerOrder(dtoA!.Id, new LeadOrderRequest { Address = shipAddr }, leadsSvc);
        var badMailOrd = await adminCtrl.CreateCustomerOrder(dtoA.Id, new LeadOrderRequest
        {
            Items = [new LeadOrderLineRequest { ProductId = "stamix-1", Quantity = 1 }],
            Email = "bad-email",
            Address = shipAddr,
        }, leadsSvc);
        check(missingCustOrd is NotFoundObjectResult && noItemsOrd is BadRequestObjectResult && badMailOrd is BadRequestObjectResult,
            "CustOrder: CreateCustomerOrder validates customer existence (404), items (400), and email format (400)");

        // 2f) Step 4-6 COD Order for blank-email/blank-address customer A + auto WhatsApp confirmation (no bogus email)
        while (sentEmails.TryDequeue(out _)) { }
        while (sentWa.TryDequeue(out _)) { }

        var ord1Res = await adminCtrl.CreateCustomerOrder(dtoA.Id, new LeadOrderRequest
        {
            Items = [new LeadOrderLineRequest { ProductId = "stamix-1", Quantity = 1 }],
            CouponCode = "cust10",
            Address = shipAddr,
            AlreadyPaid = false,
        }, leadsSvc) as ObjectResult;
        var ord1Body = ord1Res?.Value as LeadOrderResponse;
        var ord1Entity = ord1Body is null ? null : await db.Orders.AsNoTracking().SingleAsync(o => o.Id == ord1Body.OrderId);
        var custAAfterOrd1 = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == dtoA.Id);
        var leadA = await db.Leads.AsNoTracking().SingleAsync(l => l.Phone == "919840123456");
        var stockAfterOrd1 = (await db.Products.AsNoTracking().SingleAsync(p => p.Id == "stamix-1")).StockQuantity;
        var waGotOrd1 = await WaitUntilAsync(() => sentWa.Any(x => x.Body.Contains(ord1Body!.OrderId)));

        check(ord1Res?.StatusCode == 201
              && ord1Body is { Success: true, TotalAmount: 1080m }
              && ord1Entity is not null
              && ord1Entity.CustomerId == dtoA.Id
              && ord1Entity.PaymentMethod == OrderPaymentMethod.Cod
              && ord1Entity.PaymentStatus == OrderPaymentStatus.CodPending
              && ord1Entity.CustomerEmail == ""
              && (ord1Entity.Notes ?? "").Contains("Created in Customers by Admin Owner")
              && stockAfterOrd1 == 19
              && custAAfterOrd1.ShippingAddress.AddressLine1 == "12 Gandhi St"
              && leadA.Status == LeadStatus.Converted && leadA.CustomerId == dtoA.Id
              && waGotOrd1
              && sentEmails.IsEmpty,
            "CustOrder: COD order maps exact CustomerId, applies coupon, decrements stock, backfills blank address, links Lead.CustomerId, and sends WhatsApp without failing on empty email");

        // 2g) Paid Order for customer A now providing optional Email + new shipping address -> sends BOTH Email & WhatsApp and backfills Email while keeping saved address
        while (sentEmails.TryDequeue(out _)) { }
        while (sentWa.TryDequeue(out _)) { }
        var secondAddr = new AddressDto { AddressLine1 = "99 Marina Rd", City = "Chennai", State = "Tamil Nadu", Pincode = "600004" };
        var ord2Res = await adminCtrl.CreateCustomerOrder(dtoA.Id, new LeadOrderRequest
        {
            Items = [new LeadOrderLineRequest { ProductId = "stamix-1", Quantity = 2 }],
            Email = "Arun.Order@Example.Test ",
            Address = secondAddr,
            AlreadyPaid = true,
            PaymentReference = "UPI-555111",
        }, leadsSvc) as ObjectResult;
        var ord2Body = ord2Res?.Value as LeadOrderResponse;
        var ord2Entity = ord2Body is null ? null : await db.Orders.AsNoTracking().SingleAsync(o => o.Id == ord2Body.OrderId);
        var custAAfterOrd2 = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == dtoA.Id);
        var stockAfterOrd2 = (await db.Products.AsNoTracking().SingleAsync(p => p.Id == "stamix-1")).StockQuantity;
        var paymentRow2 = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.OrderId == ord2Body!.OrderId);
        var bothNotified = await WaitUntilAsync(() =>
            sentWa.Any(x => x.Body.Contains(ord2Body!.OrderId))
            && sentEmails.Any(x => x.Body.Contains(ord2Body!.OrderId) && x.Body.Contains("arun.order@example.test")));

        check(ord2Res?.StatusCode == 201
              && ord2Entity is not null
              && ord2Entity.CustomerId == dtoA.Id
              && ord2Entity.PaymentMethod == OrderPaymentMethod.Online
              && ord2Entity.PaymentStatus == OrderPaymentStatus.Paid
              && ord2Entity.PaymentRef == "UPI-555111"
              && paymentRow2 is { Status: "CAPTURED", Amount: 2400m }
              && stockAfterOrd2 == 17
              && custAAfterOrd2.Email == "arun.order@example.test"
              && custAAfterOrd2.ShippingAddress.AddressLine1 == "12 Gandhi St"
              && (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == ord1Body!.OrderId)).CustomerEmail == "arun.order@example.test"
              && bothNotified,
            "CustOrder: AlreadyPaid order marks Paid/CAPTURED, keeps existing Customer.ShippingAddress, backfills Email across prior linked orders, and sends both Email & WhatsApp confirmations");

        // 2h) Strict CustomerId isolation in Admin & Storefront lists + legacy unlinked fallback
        // Order created for customer A even if recipient details match customer B must NEVER appear under customer B
        var isolatedRes = await adminCtrl.CreateCustomerOrder(dtoA.Id, new LeadOrderRequest
        {
            Items = [new LeadOrderLineRequest { ProductId = "stamix-1", Quantity = 1 }],
            CustomerName = dtoB!.FullName,
            Phone = dtoB.Phone,
            Email = "arun.order@example.test",
            Address = shipAddr,
            AlreadyPaid = false,
        }, leadsSvc) as ObjectResult;
        var isolatedOrdId = (isolatedRes?.Value as LeadOrderResponse)!.OrderId;

        // Also seed a legacy order (CustomerId = null) matching customer B by 91-prefixed phone
        db.Orders.Add(new Order
        {
            Id = "NH-LEGACY-B",
            CustomerName = dtoB.FullName,
            CustomerPhone = "91" + dtoB.Phone,
            CustomerEmail = "",
            CustomerId = null,
            TotalAmount = 750m,
            PaymentMethod = OrderPaymentMethod.Cod,
            PaymentStatus = OrderPaymentStatus.CodPending,
            ShippingAddress = shipAddr,
        });
        await db.SaveChangesAsync();

        var adminList = await adminCtrl.ListCustomers();
        var adminA = adminList.Customers.Single(c => c.Id == dtoA.Id);
        var adminB = adminList.Customers.Single(c => c.Id == dtoB.Id);
        var portalOrdersA = await orderSvc.ListForCustomerAsync(adminA.Email, adminA.Phone, adminA.Id);
        var portalOrdersB = await orderSvc.ListForCustomerAsync(adminB.Email, adminB.Phone, adminB.Id);

        check(adminA.OrdersCount == 3
              && adminA.Orders.Any(o => o.Id == isolatedOrdId && o.CustomerId == dtoA.Id)
              && adminB.OrdersCount == 1
              && adminB.Orders.Single().Id == "NH-LEGACY-B"
              && portalOrdersA.Any(o => o.Id == isolatedOrdId)
              && portalOrdersB.Select(o => o.Id).SequenceEqual(["NH-LEGACY-B"]),
            "CustOrder: Orders with CustomerId map strictly to that Customer without leaking to other accounts while preserving unlinked legacy matching");

        // 2i) SetEmailAsync later updates CustomerId-linked orders even when previous email was empty
        var custForEmailLater = await db.Customers.FirstAsync(c => c.Id == dtoB.Id);
        var changedEmail = await custAuth.SetEmailAsync(custForEmailLater, "bala.later@example.test");
        await db.SaveChangesAsync();
        var ordForEmailLater = await adminCtrl.CreateCustomerOrder(dtoB.Id, new LeadOrderRequest
        {
            Items = [new LeadOrderLineRequest { ProductId = "stamix-1", Quantity = 1 }],
            Address = shipAddr,
        }, leadsSvc) as ObjectResult;
        var ordBLater = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == (ordForEmailLater!.Value as LeadOrderResponse)!.OrderId);
        check(changedEmail && ordBLater.CustomerEmail == "bala.later@example.test",
            "CustOrder: SetEmailAsync persists email on manual customer and subsequent orders inherit it");

        // 2j) OTP-only customer (empty Phone) gets phone prompt validation & backfill when ordering
        var otpOnly = new Customer { Id = "CUST-990011", FullName = "Otp Only", Email = "otponly@example.test", Phone = "", PasswordHash = "hash" };
        db.Customers.Add(otpOnly);
        await db.SaveChangesAsync();
        var otpNoPhoneRes = await adminCtrl.CreateCustomerOrder(otpOnly.Id, new LeadOrderRequest
        {
            Items = [new LeadOrderLineRequest { ProductId = "stamix-1", Quantity = 1 }],
            Address = shipAddr,
        }, leadsSvc);
        var otpWithPhoneRes = await adminCtrl.CreateCustomerOrder(otpOnly.Id, new LeadOrderRequest
        {
            Items = [new LeadOrderLineRequest { ProductId = "stamix-1", Quantity = 1 }],
            Phone = "+91 98222 33344",
            Address = shipAddr,
        }, leadsSvc) as ObjectResult;
        var otpReloaded = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == otpOnly.Id);
        var otpOrdEntity = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == (otpWithPhoneRes!.Value as LeadOrderResponse)!.OrderId);
        check(otpNoPhoneRes is BadRequestObjectResult
              && otpWithPhoneRes?.StatusCode == 201
              && otpReloaded.Phone == "9822233344"
              && otpOrdEntity.CustomerId == otpOnly.Id && otpOrdEntity.CustomerPhone == "9822233344",
            "CustOrder: OTP-only customer without phone requires phone at order time and backfills canonical Customer.Phone");

        // 2k) StaffController.LeadOrder links existing Customer.Id when matching customer exists, and keeps null otherwise
        var deepakRes = await adminCtrl.CreateCustomer(new AdminCreateCustomerRequest { FullName = "Deepak LeadMatch", Phone = "9840123490" }) as ObjectResult;
        var deepak = (deepakRes?.Value as CustomerResponse)!.Customer!;
        var leadWithCustomer = new Lead { Name = "Deepak Lead", Phone = "91" + deepak.Phone, Source = LeadSource.WhatsApp, Status = LeadStatus.Interested };
        var leadWithoutCustomer = new Lead { Name = "Fresh Prospect", Phone = "919900011122", Source = LeadSource.WhatsApp, Status = LeadStatus.Interested };
        db.Leads.AddRange(leadWithCustomer, leadWithoutCustomer);
        await db.SaveChangesAsync();

        var staffCtrl = new StaffController(
            db,
            leadsSvc,
            scope.ServiceProvider.GetRequiredService<WhatsAppService>(),
            orderSvc,
            scope.ServiceProvider.GetRequiredService<MediaSigner>(),
            rightsSvc,
            NullLogger<StaffController>.Instance);
        await PrepareStaffControllerAsync(staffCtrl, SuperAdminPrincipal("admin@nuraherbex.com", "Admin Owner"), scope.ServiceProvider, $"/api/admin/leads/{leadWithCustomer.Id}/order");

        var staffOrd1 = await staffCtrl.LeadOrder(leadWithCustomer.Id, new LeadOrderRequest
        {
            Items = [new LeadOrderLineRequest { ProductId = "stamix-1", Quantity = 1 }],
            Address = shipAddr,
        }) as ObjectResult;
        var staffOrd2 = await staffCtrl.LeadOrder(leadWithoutCustomer.Id, new LeadOrderRequest
        {
            Items = [new LeadOrderLineRequest { ProductId = "stamix-1", Quantity = 1 }],
            Address = shipAddr,
        }) as ObjectResult;
        var so1 = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == (staffOrd1!.Value as LeadOrderResponse)!.OrderId);
        var so2 = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == (staffOrd2!.Value as LeadOrderResponse)!.OrderId);
        await WaitUntilAsync(() => sentWa.Any(x => x.Body.Contains(so2.Id)), 1500);
        check(staffOrd1?.StatusCode == 201 && so1.CustomerId == deepak.Id && so1.CustomerPhone == deepak.Phone
              && staffOrd2?.StatusCode == 201 && so2.CustomerId is null,
            "CustOrder: StaffController.LeadOrder links existing Customer.Id on match and preserves CustomerId=null when unmatched");
    }
}
