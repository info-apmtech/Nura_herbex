using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
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
/// Offline verification that <see cref="Employee.Role"/> (<see cref="EmployeeRole"/>),
/// <see cref="Coupon.DiscountType"/> (<see cref="CouponDiscountType"/>),
/// <see cref="FollowUp.Status"/> (<see cref="FollowUpStatus"/>),
/// <see cref="FollowUp.Source"/> (<see cref="FollowUpSource"/>),
/// <see cref="LeadSourceEvent.Source"/> (<see cref="LeadSource"/>),
/// <see cref="LeadStatusHistory.FromStatus"/> / <see cref="LeadStatusHistory.ToStatus"/> (<see cref="LeadStatus"/>),
/// <see cref="Product.Status"/> (<see cref="ProductStatus"/>),
/// <see cref="Order.PaymentMethod"/> (<see cref="OrderPaymentMethod"/>),
/// <see cref="Order.PaymentStatus"/> (<see cref="OrderPaymentStatus"/>),
/// <see cref="Lead.Source"/> (<see cref="LeadSource"/>),
/// <see cref="Lead.Status"/> (<see cref="LeadStatus"/>), and
/// <see cref="WorkTask.Status"/> / <see cref="WorkTask.Priority"/> / <see cref="WorkTask.Repeat"/>
/// (<see cref="WorkTaskStatus"/> / <see cref="WorkTaskPriority"/> / <see cref="WorkTaskRepeat"/>)
/// map to SQL Server <c>int</c> columns while preserving string codes in DTOs, API endpoints, JWT claims,
/// rights, <see cref="FollowUp.CreatedBy"/> / <see cref="FollowUp.CompletedBy"/> employee ID persistence,
/// legacy <see cref="PaymentAttempt.OrderSnapshotJson"/> snapshots, and reports.
/// </summary>
public static class EnumStorageChecks
{
    private sealed class DummyHandler(List<(string Url, string Body)>? captured = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (captured is not null && request.Content is not null)
                captured.Add((request.RequestUri?.ToString() ?? "", await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"messages\":[{\"id\":\"wamid.ENUMCHECK\"}]}", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static ClaimsPrincipal SuperAdminPrincipal(string email) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Role, EmployeeRoles.Admin),
        ], "Test"));

    private static async Task PrepareStaffControllerAsync(StaffControllerBase controller, ClaimsPrincipal user, IServiceProvider sp, string path = "/api/admin/employees")
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

    public static async Task RunAsync(Action<bool, string> check)
    {
        var previousClock = FollowUpService.Clock;
        FollowUpService.Clock = () => DateTime.UtcNow;
        try
        {
        // 1) Enum integer values and string helper conversions
        check((int)EmployeeRole.Admin == 1 && (int)EmployeeRole.Sales == 2,
            "Enum: EmployeeRole values match Admin = 1, Sales = 2");
        check((int)CouponDiscountType.Percentage == 1 && (int)CouponDiscountType.Fixed == 2,
            "Enum: CouponDiscountType values match Percentage = 1, Fixed = 2");
        check((int)FollowUpStatus.Pending == 1 && (int)FollowUpStatus.Done == 2 && (int)FollowUpStatus.Skipped == 3,
            "Enum: FollowUpStatus values match Pending = 1, Done = 2, Skipped = 3");
        check((int)FollowUpSource.Manual == 1 && (int)FollowUpSource.Journey == 2 && (int)FollowUpSource.System == 3,
            "Enum: FollowUpSource values match Manual = 1, Journey = 2, System = 3");
        check((int)LeadSource.WhatsApp == 1 && (int)LeadSource.Instagram == 2 && (int)LeadSource.Facebook == 3 && (int)LeadSource.Website == 4 && (int)LeadSource.Manual == 5,
            "Enum: LeadSource values match WhatsApp = 1..Manual = 5");
        check((int)LeadStatus.New == 1 && (int)LeadStatus.Interested == 2 && (int)LeadStatus.FollowUp == 3 && (int)LeadStatus.Converted == 4 && (int)LeadStatus.NotInterested == 5,
            "Enum: LeadStatus values match New = 1..NotInterested = 5");
        check(FollowUpStatus.Pending.ToCode() == "pending" && FollowUpStatus.Done.ToCode() == "done" && FollowUpStatus.Skipped.ToCode() == "skipped"
              && FollowUpStatuses.TryParse("skipped", out var parsedFuStatus) && parsedFuStatus == FollowUpStatus.Skipped
              && !FollowUpStatuses.TryParse("unknown", out _),
            "Enum: FollowUpStatuses converts string codes cleanly");
        check(FollowUpSource.Manual.ToCode() == "manual" && FollowUpSource.Journey.ToCode() == "journey" && FollowUpSource.System.ToCode() == "system"
              && FollowUpSources.TryParse("journey", out var parsedFuSource) && parsedFuSource == FollowUpSource.Journey
              && !FollowUpSources.TryParse("unknown", out _),
            "Enum: FollowUpSources converts string codes cleanly");
        check(LeadSource.WhatsApp.ToCode() == "whatsapp" && LeadSource.Website.ToCode() == "website" && LeadSource.Manual.ToCode() == "manual"
              && LeadSources.TryParse("instagram", out var parsedLeadSource) && parsedLeadSource == LeadSource.Instagram
              && !LeadSources.TryParse("email", out _),
            "Enum: LeadSources converts string codes cleanly");
        check(LeadStatus.New.ToCode() == "new" && LeadStatus.FollowUp.ToCode() == "follow-up" && LeadStatus.NotInterested.ToCode() == "not-interested"
              && LeadStatuses.TryParse("follow-up", out var parsedLeadStatus) && parsedLeadStatus == LeadStatus.FollowUp
              && !LeadStatuses.TryParse("lost", out _),
            "Enum: LeadStatuses converts string codes cleanly");
        check(EmployeeRole.Admin.ToCode() == "admin" && EmployeeRole.Sales.ToCode() == "sales",
            "Enum: EmployeeRole.ToCode preserves 'admin' and 'sales'");
        check(CouponDiscountType.Percentage.ToCode() == "PERCENTAGE" && CouponDiscountType.Fixed.ToCode() == "FIXED",
            "Enum: CouponDiscountType.ToCode preserves 'PERCENTAGE' and 'FIXED'");
        check(EmployeeRoles.TryParse("admin", out var rAdmin) && rAdmin == EmployeeRole.Admin
              && EmployeeRoles.TryParse("sales", out var rSales) && rSales == EmployeeRole.Sales
              && !EmployeeRoles.TryParse("manager", out _) && !EmployeeRoles.TryParse(null, out _),
            "Enum: EmployeeRoles.TryParse parses valid codes and rejects invalid values");
        check(CouponDiscountTypes.TryParse("PERCENTAGE", out var dPct) && dPct == CouponDiscountType.Percentage
              && CouponDiscountTypes.TryParse("FIXED", out var dFix) && dFix == CouponDiscountType.Fixed
              && !CouponDiscountTypes.TryParse("FLAT", out _) && !CouponDiscountTypes.TryParse(null, out _),
            "Enum: CouponDiscountTypes.TryParse parses valid codes and rejects invalid values");
        check((int)ProductStatus.Active == 1 && (int)ProductStatus.Draft == 2 && (int)ProductStatus.Archived == 3
              && ProductStatus.Active.ToCode() == "active" && ProductStatus.Draft.ToCode() == "draft" && ProductStatus.Archived.ToCode() == "archived"
              && ProductStatuses.TryParse(" DRAFT ", out var pDraft) && pDraft == ProductStatus.Draft
              && !ProductStatuses.TryParse("deleted", out _),
            "Enum: ProductStatus & ProductStatuses map values and parse codes cleanly");
        check((int)OrderPaymentMethod.Cod == 1 && (int)OrderPaymentMethod.Online == 2
              && OrderPaymentMethod.Cod.ToCode() == "COD" && OrderPaymentMethod.Online.ToCode() == "ONLINE"
              && OrderPaymentMethods.TryParse(" online ", out var pmOnline) && pmOnline == OrderPaymentMethod.Online
              && !OrderPaymentMethods.TryParse("upi", out _),
            "Enum: OrderPaymentMethod & OrderPaymentMethods map values and parse codes cleanly");
        check((int)OrderPaymentStatus.Pending == 1 && (int)OrderPaymentStatus.Paid == 2 && (int)OrderPaymentStatus.Failed == 3
              && (int)OrderPaymentStatus.Refunded == 4 && (int)OrderPaymentStatus.CodPending == 5
              && (int)OrderPaymentStatus.CodCollected == 6 && (int)OrderPaymentStatus.RefundPending == 7 && (int)OrderPaymentStatus.Cancelled == 8
              && OrderPaymentStatus.CodPending.ToCode() == "COD_PENDING" && OrderPaymentStatus.RefundPending.ToCode() == "REFUND_PENDING"
              && OrderPaymentStatuses.TryParse("cod-pending", out var psCodP) && psCodP == OrderPaymentStatus.CodPending
              && OrderPaymentStatuses.TryParse("COD-COLLECTED", out var psCodC) && psCodC == OrderPaymentStatus.CodCollected
              && OrderPaymentStatuses.TryParse("CANCELED", out var psCancel) && psCancel == OrderPaymentStatus.Cancelled
              && !OrderPaymentStatuses.TryParse("CHARGEBACK", out _),
            "Enum: OrderPaymentStatus & OrderPaymentStatuses map values and parse aliases cleanly");
        check((int)WorkTaskStatus.Todo == 1 && (int)WorkTaskStatus.InProgress == 2 && (int)WorkTaskStatus.Blocked == 3 && (int)WorkTaskStatus.Done == 4
              && WorkTaskStatus.Todo.ToCode() == "todo" && WorkTaskStatus.InProgress.ToCode() == "in_progress"
              && WorkTaskStatus.Blocked.ToCode() == "blocked" && WorkTaskStatus.Done.ToCode() == "done"
              && TaskStatuses.Label(WorkTaskStatus.InProgress) == "In progress"
              && TaskStatuses.TryParse(" in_progress ", out var wtSt) && wtSt == WorkTaskStatus.InProgress
              && !TaskStatuses.TryParse("archived", out _),
            "Enum: WorkTaskStatus & TaskStatuses map values, labels and parse codes cleanly");
        check((int)WorkTaskPriority.Low == 1 && (int)WorkTaskPriority.Normal == 2 && (int)WorkTaskPriority.High == 3 && (int)WorkTaskPriority.Urgent == 4
              && WorkTaskPriority.Low.ToCode() == "low" && WorkTaskPriority.Normal.ToCode() == "normal"
              && WorkTaskPriority.High.ToCode() == "high" && WorkTaskPriority.Urgent.ToCode() == "urgent"
              && TaskPriorities.TryParse(" URGENT ", out var wtPri) && wtPri == WorkTaskPriority.Urgent
              && !TaskPriorities.TryParse("critical", out _),
            "Enum: WorkTaskPriority & TaskPriorities map values and parse codes cleanly");
        check((int)WorkTaskRepeat.None == 1 && (int)WorkTaskRepeat.Daily == 2 && (int)WorkTaskRepeat.Weekly == 3 && (int)WorkTaskRepeat.Monthly == 4
              && WorkTaskRepeat.None.ToCode() == "none" && WorkTaskRepeat.Daily.ToCode() == "daily"
              && WorkTaskRepeat.Weekly.ToCode() == "weekly" && WorkTaskRepeat.Monthly.ToCode() == "monthly"
              && TaskRepeats.TryParse(" weekly ", out var wtRep) && wtRep == WorkTaskRepeat.Weekly
              && !TaskRepeats.TryParse("hourly", out _),
            "Enum: WorkTaskRepeat & TaskRepeats map values and parse codes cleanly");

        var legacyOrderJson = """{"Id":"NH-JSON1","CustomerName":"Json Tester","CustomerPhone":"9999900001","PaymentMethod":"ONLINE","PaymentStatus":"COD_PENDING"}""";
        var parsedLegacyOrder = JsonSerializer.Deserialize<Order>(legacyOrderJson);
        var canceledJson = """{"Id":"NH-JSON2","PaymentMethod":2,"PaymentStatus":"CANCELED"}""";
        var parsedCanceledOrder = JsonSerializer.Deserialize<Order>(canceledJson);
        var reserializedOrderJson = JsonSerializer.Serialize(parsedLegacyOrder!);
        check(parsedLegacyOrder is { PaymentMethod: OrderPaymentMethod.Online, PaymentStatus: OrderPaymentStatus.CodPending }
              && parsedCanceledOrder is { PaymentMethod: OrderPaymentMethod.Online, PaymentStatus: OrderPaymentStatus.Cancelled }
              && reserializedOrderJson.Contains("\"PaymentMethod\":\"ONLINE\"")
              && reserializedOrderJson.Contains("\"PaymentStatus\":\"COD_PENDING\""),
            "Enum: Order JSON converters deserialize legacy strings & ints and serialize string wire codes");

        // 2) EF Core SQL Server provider model metadata check (no DB network connection opened)
        var sqlOptions = new DbContextOptionsBuilder<NuraDbContext>()
            .UseSqlServer("Server=.;Database=NuraherbexCheck;Trusted_Connection=True;")
            .Options;
        await using (var sqlDb = new NuraDbContext(sqlOptions))
        {
            var empRoleProp = sqlDb.Model.FindEntityType(typeof(Employee))!.FindProperty(nameof(Employee.Role))!;
            var couponTypeProp = sqlDb.Model.FindEntityType(typeof(Coupon))!.FindProperty(nameof(Coupon.DiscountType))!;
            var fuStatusProp = sqlDb.Model.FindEntityType(typeof(FollowUp))!.FindProperty(nameof(FollowUp.Status))!;
            var fuSourceProp = sqlDb.Model.FindEntityType(typeof(FollowUp))!.FindProperty(nameof(FollowUp.Source))!;
            var leadSourceProp = sqlDb.Model.FindEntityType(typeof(LeadSourceEvent))!.FindProperty(nameof(LeadSourceEvent.Source))!;
            var histFromProp = sqlDb.Model.FindEntityType(typeof(LeadStatusHistory))!.FindProperty(nameof(LeadStatusHistory.FromStatus))!;
            var histToProp = sqlDb.Model.FindEntityType(typeof(LeadStatusHistory))!.FindProperty(nameof(LeadStatusHistory.ToStatus))!;
            var prodStatusProp = sqlDb.Model.FindEntityType(typeof(Product))!.FindProperty(nameof(Product.Status))!;
            var orderMethodProp = sqlDb.Model.FindEntityType(typeof(Order))!.FindProperty(nameof(Order.PaymentMethod))!;
            var orderPayStatusProp = sqlDb.Model.FindEntityType(typeof(Order))!.FindProperty(nameof(Order.PaymentStatus))!;
            var leadEntitySourceProp = sqlDb.Model.FindEntityType(typeof(Lead))!.FindProperty(nameof(Lead.Source))!;
            var leadEntityStatusProp = sqlDb.Model.FindEntityType(typeof(Lead))!.FindProperty(nameof(Lead.Status))!;
            var taskStatusProp = sqlDb.Model.FindEntityType(typeof(WorkTask))!.FindProperty(nameof(WorkTask.Status))!;
            var taskPriorityProp = sqlDb.Model.FindEntityType(typeof(WorkTask))!.FindProperty(nameof(WorkTask.Priority))!;
            var taskRepeatProp = sqlDb.Model.FindEntityType(typeof(WorkTask))!.FindProperty(nameof(WorkTask.Repeat))!;
            var fuIndex = sqlDb.Model.FindEntityType(typeof(FollowUp))!.GetIndexes()
                .Any(ix => ix.Properties.Select(p => p.Name).SequenceEqual([nameof(FollowUp.AssignedToEmployeeId), nameof(FollowUp.Status), nameof(FollowUp.DueAt)]));
            var leadStatusIndex = sqlDb.Model.FindEntityType(typeof(Lead))!.GetIndexes()
                .Any(ix => ix.Properties.Select(p => p.Name).SequenceEqual([nameof(Lead.Status), nameof(Lead.LastContactAt)]));
            var taskStatusIndex = sqlDb.Model.FindEntityType(typeof(WorkTask))!.GetIndexes()
                .Any(ix => ix.Properties.Select(p => p.Name).SequenceEqual([nameof(WorkTask.AssignedToEmployeeId), nameof(WorkTask.Status), nameof(WorkTask.DueAt)]));
            check(empRoleProp.GetColumnType() == "int" && empRoleProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int),
                "Enum: Employee.Role maps to SQL Server int column via enum converter");
            check(couponTypeProp.GetColumnType() == "int" && couponTypeProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int),
                "Enum: Coupon.DiscountType maps to SQL Server int column via enum converter");
            check(fuStatusProp.GetColumnType() == "int" && !fuStatusProp.IsNullable && fuStatusProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int) && fuIndex,
                "Enum: FollowUp.Status maps to non-null SQL Server int column and keeps composite index");
            check(fuSourceProp.GetColumnType() == "int" && !fuSourceProp.IsNullable && fuSourceProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int),
                "Enum: FollowUp.Source maps to non-null SQL Server int column");
            check(leadSourceProp.GetColumnType() == "int" && !leadSourceProp.IsNullable && leadSourceProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int),
                "Enum: LeadSourceEvent.Source maps to non-null SQL Server int column");
            check(histFromProp.GetColumnType() == "int" && histFromProp.IsNullable && histFromProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int),
                "Enum: LeadStatusHistory.FromStatus maps to nullable SQL Server int column");
            check(histToProp.GetColumnType() == "int" && !histToProp.IsNullable && histToProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int),
                "Enum: LeadStatusHistory.ToStatus maps to non-null SQL Server int column");
            check(prodStatusProp.GetColumnType() == "int" && !prodStatusProp.IsNullable && prodStatusProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int),
                "Enum: Product.Status maps to non-null SQL Server int column via enum converter");
            check(orderMethodProp.GetColumnType() == "int" && !orderMethodProp.IsNullable && orderMethodProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int),
                "Enum: Order.PaymentMethod maps to non-null SQL Server int column via enum converter");
            check(orderPayStatusProp.GetColumnType() == "int" && !orderPayStatusProp.IsNullable && orderPayStatusProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int),
                "Enum: Order.PaymentStatus maps to non-null SQL Server int column via enum converter");
            check(leadEntitySourceProp.GetColumnType() == "int" && !leadEntitySourceProp.IsNullable && leadEntitySourceProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int),
                "Enum: Lead.Source maps to non-null SQL Server int column via enum converter");
            check(leadEntityStatusProp.GetColumnType() == "int" && !leadEntityStatusProp.IsNullable && leadEntityStatusProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int) && leadStatusIndex,
                "Enum: Lead.Status maps to non-null SQL Server int column and keeps composite index");
            check(taskStatusProp.GetColumnType() == "int" && !taskStatusProp.IsNullable && taskStatusProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int) && taskStatusIndex,
                "Enum: WorkTask.Status maps to non-null SQL Server int column and keeps composite index");
            check(taskPriorityProp.GetColumnType() == "int" && !taskPriorityProp.IsNullable && taskPriorityProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int),
                "Enum: WorkTask.Priority maps to non-null SQL Server int column via enum converter");
            check(taskRepeatProp.GetColumnType() == "int" && !taskRepeatProp.IsNullable && taskRepeatProp.GetTypeMapping().Converter?.ProviderClrType == typeof(int),
                "Enum: WorkTask.Repeat maps to non-null SQL Server int column via enum converter");
        }

        // 3) End-to-end API/Service checks on InMemory DbContext
        var dbName = "enum-checks-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddDbContext<NuraDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<StaffAvailability>();
        services.AddScoped<MessageTemplateService>();
        services.AddScoped<LeadService>();
        services.AddScoped<TaskService>();
        services.AddScoped<RightsService>();
        await using var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NuraDbContext>();
        var templates = scope.ServiceProvider.GetRequiredService<MessageTemplateService>();
        var leads = scope.ServiceProvider.GetRequiredService<LeadService>();
        var rights = scope.ServiceProvider.GetRequiredService<RightsService>();
        var cache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();
        var waOptions = Options.Create(new WhatsAppOptions { AccessToken = "x", PhoneNumberId = "1", DefaultCountryCode = "91" });
        var storeOptions = Options.Create(new StoreOptions { FrontendUrl = "https://shop.test" });
        var jwtOptions = Options.Create(new JwtOptions { Secret = new string('k', 40), Issuer = "nura", Audience = "nura" });
        var whatsapp = new WhatsAppService(db, new HttpClient(new DummyHandler()), leads, waOptions, NullLogger<WhatsAppService>.Instance);
        var shippingStore = new ShippingSettingsStore();
        var shippingGateway = new ShippingGateway(
            new ShiprocketService(new HttpClient(new DummyHandler()), Options.Create(new ShiprocketOptions()), shippingStore, NullLogger<ShiprocketService>.Instance),
            new ShadowfaxService(new HttpClient(new DummyHandler()), Options.Create(new ShadowfaxOptions()), shippingStore, NullLogger<ShadowfaxService>.Instance),
            shippingStore,
            Options.Create(new ShippingOptions()),
            Options.Create(new ShadowfaxOptions()));
        var productSvc = new ProductService(db, storeOptions);
        var opLock = new OrderOperationLock(db);
        var orderSvc = new OrderService(db, productSvc, shippingGateway, opLock, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<OrderService>.Instance);
        var tokenSvc = new TokenService(jwtOptions);
        var sentEmails = new List<(string Url, string Body)>();
        var emailSvc = new EmailService(new HttpClient(new DummyHandler(sentEmails)), Options.Create(new EmailOptions { ResendApiKey = "test-resend-key" }), storeOptions, NullLogger<EmailService>.Instance);
        var customerAuth = new CustomerAuthService(db, tokenSvc, emailSvc, cache, orderSvc);
        var signer = new MediaSigner(jwtOptions);

        var staffCtrl = new StaffController(db, leads, whatsapp, orderSvc, signer, rights, NullLogger<StaffController>.Instance);
        var catalogCtrl = new AdminCatalogController(db);
        var adminCtrl = new AdminController(db, orderSvc, customerAuth, new SettingsService(db), tokenSvc, emailSvc, whatsapp, new FixedMonitor<AdminOptions>(new AdminOptions()), waOptions, rights, signer, NullLogger<AdminController>.Instance);

        await PrepareStaffControllerAsync(staffCtrl, SuperAdminPrincipal("admin@nuraherbex.com"), scope.ServiceProvider);

        // Create sales employee via API DTO ("sales") -> saved as EmployeeRole.Sales (2)
        var createRes = await staffCtrl.CreateEmployee(new EmployeeSaveRequest
        {
            Name = "Arun Sales",
            Email = "arun.sales@nuraherbex.com",
            Phone = "9876543210",
            Role = "sales",
            Password = "Password123!",
            IsActive = true,
        }) as ObjectResult;
        var createdDto = (createRes?.Value as EmployeeResponse)?.Employee;
        check(createRes?.StatusCode == 201 && createdDto is { Role: "sales" },
            "Enum: StaffController.CreateEmployee accepts 'sales' and returns 'sales' DTO");

        var empRow = await db.Employees.SingleAsync(e => e.Email == "arun.sales@nuraherbex.com");
        check(empRow.Role == EmployeeRole.Sales && (int)empRow.Role == 2,
            "Enum: Created employee entity stores EmployeeRole.Sales (2)");

        // Staff login & JWT role claim before promotion
        var salesLogin = await adminCtrl.Login(new AdminLoginRequest { Email = "arun.sales@nuraherbex.com", Password = "Password123!" }) as OkObjectResult;
        var salesLoginBody = salesLogin?.Value as AdminLoginResponse;
        var salesJwt = new JwtSecurityTokenHandler().ReadJwtToken(salesLoginBody!.Token);
        check(salesLoginBody.User?.Role == "sales"
              && salesJwt.Claims.Any(c => (c.Type == ClaimTypes.Role || c.Type == "role") && c.Value == "sales"),
            "Enum: Staff login DTO and JWT claim emit 'sales' for EmployeeRole.Sales");

        // Promote to admin via UpdateEmployee("admin") -> saved as EmployeeRole.Admin (1)
        var updateRes = await staffCtrl.UpdateEmployee(empRow.Id, new EmployeeSaveRequest
        {
            Name = empRow.Name,
            Email = empRow.Email,
            Phone = empRow.Phone,
            Role = "admin",
            IsActive = true,
        }) as OkObjectResult;
        var updatedDto = (updateRes?.Value as EmployeeResponse)?.Employee;
        await db.Entry(empRow).ReloadAsync();
        check(updatedDto?.Role == "admin" && empRow.Role == EmployeeRole.Admin && (int)empRow.Role == 1,
            "Enum: StaffController.UpdateEmployee saves EmployeeRole.Admin (1) and returns 'admin' DTO");

        var resolvedAdmin = await rights.ResolveEmployeeAsync(empRow.Id);
        check(resolvedAdmin.Super && resolvedAdmin.Active && resolvedAdmin.Level("team") == PageCatalog.Full && resolvedAdmin.All("leads"),
            "Enum: RightsService grants full super-admin rights when Employee.Role == EmployeeRole.Admin");

        var adminJwt = new JwtSecurityTokenHandler().ReadJwtToken(tokenSvc.CreateStaffToken(empRow));
        check(adminJwt.Claims.Any(c => (c.Type == ClaimTypes.Role || c.Type == "role") && c.Value == "admin"),
            "Enum: TokenService emits 'admin' JWT role claim for EmployeeRole.Admin");

        // Team projection & invalid role rejection
        var teamRes = await staffCtrl.Team("tasks") as OkObjectResult;
        var teamBody = teamRes?.Value as EmployeeListResponse;
        check(teamBody?.Employees.Any(e => e.Id == empRow.Id && e.Role == "admin") == true,
            "Enum: StaffController.Team SQL projection maps EmployeeRole enum back to string code");

        var badRoleRes = await staffCtrl.CreateEmployee(new EmployeeSaveRequest
        {
            Name = "Invalid Role",
            Email = "bad.role@nuraherbex.com",
            Role = "superadmin",
            Password = "Password123!",
        });
        check(badRoleRes is BadRequestObjectResult,
            "Enum: StaffController rejects unknown role strings with 400 BadRequest");

        // Coupon Create/Update/List + ProductService discount calculation
        var couponCreateRes = await catalogCtrl.CreateCoupon(new CouponDto
        {
            Code = "save25",
            DiscountType = "PERCENTAGE",
            DiscountValue = 25m,
            MinOrderAmount = 500m,
            MaxDiscountCap = 200m,
            IsActive = true,
        }) as ObjectResult;
        var createdCoupon = (couponCreateRes?.Value as AdminCouponResponse)?.Coupon;
        var couponRow = await db.Coupons.SingleAsync(c => c.Code == "SAVE25");
        check(couponCreateRes?.StatusCode == 201
              && createdCoupon?.DiscountType == "PERCENTAGE"
              && couponRow.DiscountType == CouponDiscountType.Percentage
              && (int)couponRow.DiscountType == 1,
            "Enum: AdminCatalogController.CreateCoupon stores CouponDiscountType.Percentage (1) and returns 'PERCENTAGE'");

        var pctCheck = await productSvc.ValidateCouponAsync("SAVE25", 1000m);
        check(pctCheck.Valid && pctCheck.Discount == 200m,
            "Enum: ProductService applies percentage discount with MaxDiscountCap");

        var couponUpdateRes = await catalogCtrl.UpdateCoupon("SAVE25", new CouponDto
        {
            Code = "SAVE25",
            DiscountType = "FIXED",
            DiscountValue = 150m,
            MinOrderAmount = 0m,
            MaxDiscountCap = 0m,
            IsActive = true,
        }) as ObjectResult;
        var updatedCoupon = (couponUpdateRes?.Value as AdminCouponResponse)?.Coupon;
        await db.Entry(couponRow).ReloadAsync();
        check(couponUpdateRes?.StatusCode == 200
              && updatedCoupon?.DiscountType == "FIXED"
              && couponRow.DiscountType == CouponDiscountType.Fixed
              && (int)couponRow.DiscountType == 2,
            "Enum: AdminCatalogController.UpdateCoupon stores CouponDiscountType.Fixed (2) and returns 'FIXED'");

        var fixedCheck = await productSvc.ValidateCouponAsync("SAVE25", 1000m);
        check(fixedCheck.Valid && fixedCheck.Discount == 150m,
            "Enum: ProductService applies fixed discount for CouponDiscountType.Fixed");

        var couponsList = await catalogCtrl.Coupons();
        check(couponsList.Coupons.Any(c => c.Code == "SAVE25" && c.DiscountType == "FIXED"),
            "Enum: AdminCatalogController.Coupons returns string DiscountType");

        var badCouponRes = await catalogCtrl.CreateCoupon(new CouponDto
        {
            Code = "BAD10",
            DiscountType = "FLAT",
            DiscountValue = 10m,
        });
        check(badCouponRes is BadRequestObjectResult,
            "Enum: AdminCatalogController rejects unknown DiscountType strings with 400 BadRequest");

        // FollowUp enums, LeadSourceEvent, LeadStatusHistory, ID persistence & resolution
        var inbound = await leads.UpsertFromWhatsAppAsync("919988776655", "Meera", "Hi", "{\"source_id\":\"101\"}", "Ad 101");
        var srcEv = await db.LeadSourceEvents.SingleAsync(e => e.LeadId == inbound.Lead.Id);
        var initHist = await db.LeadStatusHistory.SingleAsync(h => h.LeadId == inbound.Lead.Id);
        check(srcEv.Source == LeadSource.WhatsApp && (int)srcEv.Source == 1
              && initHist.FromStatus is null && initHist.ToStatus == LeadStatus.New && (int)initHist.ToStatus == 1,
            "Enum: Inbound WhatsApp creates LeadSourceEvent.Source=WhatsApp (1) and LeadStatusHistory From=null To=New (1)");

        inbound.Lead.AssignedToEmployeeId = empRow.Id;
        await db.SaveChangesAsync();

        var staffUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, empRow.Email),
            new Claim(ClaimTypes.Name, empRow.Name),
            new Claim(ClaimTypes.Role, empRow.Role.ToCode()),
            new Claim("emp_id", empRow.Id.ToString()),
        ], "Test"));
        await PrepareStaffControllerAsync(staffCtrl, staffUser, scope.ServiceProvider, $"/api/admin/leads/{inbound.Lead.Id}/status");
        await staffCtrl.LeadStatus(inbound.Lead.Id, new LeadStatusRequest { Status = LeadStatuses.Interested, Note = "Call requested" });

        var fuCtrl = new FollowUpController(db, new FollowUpService(db, leads, whatsapp, Options.Create(new FollowUpOptions()), storeOptions, NullLogger<FollowUpService>.Instance, waOptions, templates), whatsapp, rights);
        var fuHttp = new DefaultHttpContext { User = staffUser, RequestServices = scope.ServiceProvider };
        fuHttp.Request.Path = "/api/admin/presence/offline";
        var fuAct = new ActionContext(fuHttp, new RouteData(), new ControllerActionDescriptor());
        fuCtrl.ControllerContext = new ControllerContext(fuAct);
        await fuCtrl.OnActionExecutionAsync(new ActionExecutingContext(fuAct, [], new Dictionary<string, object?>(), fuCtrl),
            () => { fuHttp.Request.Path = "/api/admin/followups"; return Task.FromResult(new ActionExecutedContext(fuAct, [], fuCtrl)); });

        await fuCtrl.Create(new FollowUpCreateRequest { LeadId = inbound.Lead.Id, Type = FollowUpTypes.WhatsApp, Title = "Send info", DueAt = DateTime.UtcNow.AddDays(1) });
        await fuCtrl.Create(new FollowUpCreateRequest { LeadId = inbound.Lead.Id, Type = FollowUpTypes.Call, Title = "Skip check", DueAt = DateTime.UtcNow.AddDays(2) });
        var fus = await db.FollowUps.Where(f => f.LeadId == inbound.Lead.Id).OrderBy(f => f.Id).ToListAsync();
        fus[0].SuggestedMessage = "Hello Meera!";
        await db.SaveChangesAsync();

        check(fus.All(f => f.Status == FollowUpStatus.Pending && f.Source == FollowUpSource.Manual && f.CreatedBy == empRow.Id.ToString()),
            "Enum: FollowUp create saves Status=Pending (1), Source=Manual (1), and employee ID in CreatedBy");

        await fuCtrl.Send(fus[0].Id);
        await fuCtrl.Skip(fus[1].Id);
        await db.Entry(fus[0]).ReloadAsync();
        await db.Entry(fus[1]).ReloadAsync();
        var waSentMsg = await db.WhatsAppMessages.OrderByDescending(m => m.Id).FirstAsync(m => m.Phone == inbound.Lead.Phone);
        check(fus[0].Status == FollowUpStatus.Done && fus[0].CompletedBy == empRow.Id.ToString() && waSentMsg.SentBy == empRow.Name
              && fus[1].Status == FollowUpStatus.Skipped && fus[1].CompletedBy == empRow.Id.ToString(),
            "Enum: FollowUp Send/Skip persist Status=Done(2)/Skipped(3) & employee ID in CompletedBy while WhatsApp SentBy stays readable name");

        empRow.Name = "Arun Renamed";
        await db.SaveChangesAsync();

        var fuList = await fuCtrl.List("done", null, null, inbound.Lead.Id);
        check(fuList.Items.Count == 2 && fuList.Items.All(i => i.CompletedBy == "Arun Renamed" && i.Source == "manual")
              && fuList.Items.Any(i => i.Status == "done") && fuList.Items.Any(i => i.Status == "skipped"),
            "Enum: FollowUpController.List resolves CompletedBy ID to current employee name and returns string status/source");

        var reportSvc = new ReportService(db, whatsapp);
        var fuReport = await reportSvc.BuildAsync("followups", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(5), "Test", null);
        check(fuReport is not null && fuReport.Rows.Count == 2 && fuReport.Rows.All(r => r[9] == "Arun Renamed" && r[13] == "manual"),
            "Enum: ReportService FollowUps report resolves CompletedBy ID to employee name and emits string status/source");

        var detailRes = await staffCtrl.LeadDetail(inbound.Lead.Id) as OkObjectResult;
        var detail = detailRes?.Value as LeadDetailResponse;
        check(detail is not null
              && detail.History.Any(h => h.FromStatus == "new" && h.ToStatus == "interested")
              && detail.Audit.Any(a => a.Summary == "Status new → interested")
              && detail.FollowUps.All(f => f.CompletedBy == "Arun Renamed"),
            "Enum: LeadDetail maps LeadStatus enum history/audit and FollowUp CompletedBy back to readable strings");

        // 4) Phase 4 end-to-end checks: Product.Status, Order.PaymentMethod/PaymentStatus, Lead.Source/Status
        var newProdRes = await catalogCtrl.CreateProduct(new AdminProductDto
        {
            Sku = "PHASE4-1", Name = "Stamix Phase 4", Price = 500m, CostPrice = 200m, StockQuantity = 10,
            WeightKg = 0.5m, LengthCm = 10, BreadthCm = 10, HeightCm = 10, Status = ProductStatuses.Draft,
        }) as ObjectResult;
        var createdProd = (newProdRes?.Value as AdminProductResponse)?.Product;
        var prodEntity = await db.Products.SingleAsync(p => p.Id == createdProd!.Id);
        check(newProdRes?.StatusCode == 201 && createdProd?.Status == "draft"
              && prodEntity.Status == ProductStatus.Draft && (int)prodEntity.Status == 2
              && !(await productSvc.ListActiveAsync()).Any(p => p.Id == prodEntity.Id),
            "Enum: Draft product stores ProductStatus.Draft (2) and stays hidden from active shop catalog");

        var uppercaseStatusBad = await catalogCtrl.CreateProduct(new AdminProductDto
        {
            Sku = "PHASE4-BADCASE", Name = "Bad Case", Price = 500m, StockQuantity = 5,
            WeightKg = 0.5m, LengthCm = 10, BreadthCm = 10, HeightCm = 10, Status = "ACTIVE",
        });
        check(uppercaseStatusBad is BadRequestObjectResult,
            "Enum: Product validation preserves exact lowercase status check");

        createdProd!.Status = ProductStatuses.Active;
        var pubRes = await catalogCtrl.UpdateProduct(createdProd.Id, createdProd) as ObjectResult;
        var pubProd = (pubRes?.Value as AdminProductResponse)?.Product;
        await db.Entry(prodEntity).ReloadAsync();
        check(pubRes?.StatusCode == 200 && pubProd?.Status == "active" && prodEntity.Status == ProductStatus.Active
              && (await productSvc.ListActiveAsync()).Any(p => p.Id == prodEntity.Id),
            "Enum: Updating product to active stores ProductStatus.Active (1) and surfaces in shop");

        var addr = new AddressDto { AddressLine1 = "12 MG Road", City = "Chennai", State = "Tamil Nadu", Pincode = "600001", Country = "India" };
        var codOrder = await orderSvc.CreateAsync(new CreateOrderRequest
        {
            PaymentMethod = OrderPaymentMethods.Cod,
            Customer = new CheckoutCustomerDto { FullName = "Ravi COD", Email = "ravi.cod@x.test", Phone = "9876500111" },
            Shipping = addr,
            Items = [new CartLineDto { Id = prodEntity.Id, Quantity = 2 }],
        }, customerId: null);
        check(codOrder.PaymentMethod == OrderPaymentMethod.Cod && (int)codOrder.PaymentMethod == 1
              && codOrder.PaymentStatus == OrderPaymentStatus.CodPending && (int)codOrder.PaymentStatus == 5,
            "Enum: COD order stores PaymentMethod.Cod (1) and PaymentStatus.CodPending (5)");

        var heldOnlineOrder = await orderSvc.CreateAsync(new CreateOrderRequest
        {
            PaymentMethod = OrderPaymentMethods.Online,
            Customer = new CheckoutCustomerDto { FullName = "Meera Online", Email = "meera.online@x.test", Phone = "9876500222" },
            Shipping = addr,
            Items = [new CartLineDto { Id = prodEntity.Id, Quantity = 1 }],
        }, customerId: null, holdUntilPaid: true);
        var heldAttempt = await db.PaymentAttempts.SingleAsync(a => a.OrderId == heldOnlineOrder.Id);
        check(heldOnlineOrder.PaymentMethod == OrderPaymentMethod.Online
              && heldOnlineOrder.PaymentStatus == OrderPaymentStatus.Pending
              && heldAttempt.OrderSnapshotJson.Contains("\"PaymentMethod\":\"ONLINE\"")
              && heldAttempt.OrderSnapshotJson.Contains("\"PaymentStatus\":\"PENDING\""),
            "Enum: Online held order persists PaymentMethod.Online (2), PaymentStatus.Pending (1) and string codes in OrderSnapshotJson");

        var payuSvc = new PayUService(db, orderSvc, opLock, Options.Create(new PayUOptions { MerchantKey = "k", MerchantSalt = "s" }), storeOptions, NullLogger<PayUService>.Instance);
        var init = await payuSvc.InitiateAsync(heldOnlineOrder.Id, "https://api.test");
        check(init.Success && init.Fields["udf1"] == heldOnlineOrder.Id,
            "Enum: PayUService.InitiateAsync works for OrderPaymentStatus.Pending");
        await orderSvc.MarkPaidAsync(heldOnlineOrder.Id, "PAYU-1", "TXN-1", "{}");
        await db.Entry(heldOnlineOrder).ReloadAsync();
        check(heldOnlineOrder.PaymentStatus == OrderPaymentStatus.Paid,
            "Enum: OrderService.MarkPaidAsync sets PaymentStatus.Paid");

        // Recovering a pre-Phase-4 PaymentAttempt whose OrderSnapshotJson holds legacy string payment codes
        var legacyAttempt = new PaymentAttempt
        {
            OrderId = "NH-LEG1",
            TxnId = "TXN-LEG1",
            Amount = 500m,
            Status = "INITIATED",
            CustomerName = "Legacy Buyer",
            CustomerEmail = "legacy@x.test",
            CustomerPhone = "9876500333",
            OrderSnapshotJson = $$"""{"Id":"NH-LEG1","CustomerName":"Legacy Buyer","CustomerEmail":"legacy@x.test","CustomerPhone":"9876500333","ShippingAddress":{"AddressLine1":"1 Test Rd","City":"Chennai","State":"Tamil Nadu","Pincode":"600001","Country":"India"},"Items":[{"Id":"{{prodEntity.Id}}","Sku":"PHASE4-1","Name":"Stamix Phase 4","Price":500,"Quantity":1,"Total":500}],"Subtotal":500,"TotalAmount":500,"PaymentMethod":"ONLINE","PaymentStatus":"PENDING","FulfillmentStatus":"PENDING","DeliveryStatus":"Order Placed","DeliveryTrackingEvents":[]}""",
        };
        db.PaymentAttempts.Add(legacyAttempt);
        await db.SaveChangesAsync();
        var recovered = await orderSvc.ConfirmHeldOrderAsync(legacyAttempt, "PAYU-LEG", "TXN-LEG1", "{}");
        check(recovered.PaymentMethod == OrderPaymentMethod.Online && recovered.PaymentStatus == OrderPaymentStatus.Paid,
            "Enum: ConfirmHeldOrderAsync deserializes legacy string PaymentMethod/PaymentStatus snapshot and marks Paid");

        var refundMsg = await orderSvc.CancelOrderAsync("NH-LEG1", "Customer cancel", byCustomer: true);
        var recoveredAfterCancel = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == "NH-LEG1");
        check(recoveredAfterCancel.PaymentStatus == OrderPaymentStatus.RefundPending && refundMsg.Contains("refunded"),
            "Enum: Cancelling paid order moves PaymentStatus to RefundPending");
        await orderSvc.MarkRefundedAsync("NH-LEG1");
        var recoveredAfterRefund = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == "NH-LEG1");
        check(recoveredAfterRefund.PaymentStatus == OrderPaymentStatus.Refunded,
            "Enum: MarkRefundedAsync transitions PaymentStatus to Refunded");
        await orderSvc.DeleteOrderAsync("NH-LEG1");
        check(!await db.Orders.AnyAsync(o => o.Id == "NH-LEG1"),
            "Enum: Refunded order can be deleted while RefundPending cannot");

        // StaffController.Leads filtering & LeadOrder + CRM Dashboard + Reports
        await PrepareStaffControllerAsync(staffCtrl, SuperAdminPrincipal("admin@nuraherbex.com"), scope.ServiceProvider);
        var adminLeadRes = await staffCtrl.Leads("follow-up", "whatsapp", null, null, 100, LeadViews.Leads);
        var unknownSrcRes = await staffCtrl.Leads(null, "unknown_source", null, null, 100, LeadViews.Leads);
        var unknownStatRes = await staffCtrl.Leads("unknown_status", null, null, null, 100, LeadViews.Leads);
        await staffCtrl.LeadStatus(inbound.Lead.Id, new LeadStatusRequest { Status = LeadStatuses.Interested, Note = "Ready for hot leads view" });
        var hotLeadRes = await staffCtrl.Leads("interested", "whatsapp", null, null, 100, LeadViews.Interested);
        check(adminLeadRes.Total == 1 && adminLeadRes.Leads[0].Source == "whatsapp" && adminLeadRes.Leads[0].Status == "follow-up"
              && adminLeadRes.StatusCounts.TryGetValue("follow-up", out var cntFu) && cntFu == 1
              && hotLeadRes.Total == 1 && hotLeadRes.Leads[0].Status == "interested"
              && unknownSrcRes.Total == 0 && unknownSrcRes.Leads.Count == 0
              && unknownStatRes.Total == 0 && unknownStatRes.Leads.Count == 0,
            "Enum: StaffController.Leads filters by LeadSource/LeadStatus enums and returns 0 rows for unknown filter strings");

        var crmCodOrderRes = await staffCtrl.LeadOrder(inbound.Lead.Id, new LeadOrderRequest
        {
            Items = [new LeadOrderLineRequest { ProductId = prodEntity.Id, Quantity = 1 }],
            Address = addr,
            AlreadyPaid = false,
            Email = "meera@x.test",
        }) as ObjectResult;
        var crmCodOrderId = (crmCodOrderRes?.Value as LeadOrderResponse)?.OrderId;
        var crmCodEntity = await db.Orders.SingleAsync(o => o.Id == crmCodOrderId);
        check(crmCodOrderRes?.StatusCode == 201 && crmCodEntity.PaymentMethod == OrderPaymentMethod.Cod && crmCodEntity.PaymentStatus == OrderPaymentStatus.CodPending,
            "Enum: StaffController.LeadOrder COD saves OrderPaymentMethod.Cod and OrderPaymentStatus.CodPending");

        var detailAfterOrder = (await staffCtrl.LeadDetail(inbound.Lead.Id) as OkObjectResult)?.Value as LeadDetailResponse;
        check(detailAfterOrder?.Lead?.Status == "converted"
              && detailAfterOrder.Orders.Any(o => o.Id == crmCodOrderId && o.PaymentMethod == "COD" && o.PaymentStatus == "COD_PENDING"),
            "Enum: LeadDetail maps converted lead and LeadOrderDto payment fields back to canonical string codes");

        var dash = await new CrmDashboardService(db).BuildAsync(30, null);
        check(dash.ConvertedLeads >= 1 && dash.Funnel.Any(f => f.Status == "converted" && f.Count >= 1)
              && dash.BySource.Any(s => s.Source == "whatsapp") && dash.Orders >= 1 && dash.Revenue >= 500m,
            "Enum: CrmDashboardService aggregates LeadStatus/LeadSource/OrderPaymentStatus enums with string keys");

        var ordersRep = await reportSvc.BuildAsync("orders", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), "Today", null);
        var leadsRep = await reportSvc.BuildAsync("leads", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), "Today", null);
        check(ordersRep is not null && ordersRep.Rows.Any(r => r[12] == "COD" && r[13] == "COD_PENDING")
              && leadsRep is not null && leadsRep.Rows.Any(r => r[5] == "whatsapp" && r[7] == "converted")
              && (leadsRep.Summary.First(s => s.Label == "Top source").Value.StartsWith("whatsapp (")),
            "Enum: ReportService Orders & Leads emit lowercase/canonical string codes including Top source summary");

        var invoiceHtmlPaid = new InvoiceService(jwtOptions, new FixedMonitor<StoreOptions>(storeOptions.Value)).RenderHtml(heldOnlineOrder);
        var invoiceHtmlCod = new InvoiceService(jwtOptions, new FixedMonitor<StoreOptions>(storeOptions.Value)).RenderHtml(codOrder);
        var waStatusCod = WhatsAppService.BuildStatusText(codOrder);
        var waStatusPaid = WhatsAppService.BuildStatusText(heldOnlineOrder);
        var emailSentOk = await emailSvc.SendOrderConfirmationAsync(codOrder);
        var replySuggestion = await new ReplyAssistantService(db, Options.Create(new AiOptions()), storeOptions, cache, NullLogger<ReplyAssistantService>.Instance)
            .SuggestAsync(inbound.Lead, new ReplySuggestRequest { Fresh = true });
        check(invoiceHtmlPaid.Contains("Online Payment") && invoiceHtmlPaid.Contains("PAID")
              && invoiceHtmlCod.Contains("Cash on Delivery") && invoiceHtmlCod.Contains("PAYMENT DUE")
              && waStatusCod.Contains("Cash on Delivery") && waStatusPaid.Contains("PAID")
              && emailSentOk && sentEmails.Any(e => e.Body.Contains("Cash on Delivery"))
              && replySuggestion.Success,
            "Enum: InvoiceService, EmailService, WhatsAppService and ReplyAssistantService render identical customer text");

        await orderSvc.CancelOrderAsync(codOrder.Id, "Check restore", byCustomer: false);
        var codCancelled = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == codOrder.Id);
        var archRes = await catalogCtrl.ArchiveProduct(prodEntity.Id);
        var archivedProd = await db.Products.AsNoTracking().SingleAsync(p => p.Id == prodEntity.Id);
        bool calcThrewArchived = false;
        try { await productSvc.CalculateAsync([new CartLineDto { Id = prodEntity.Id, Quantity = 1 }], null); }
        catch (InvalidOperationException) { calcThrewArchived = true; }
        check(codCancelled.PaymentStatus == OrderPaymentStatus.Cancelled
              && archRes is OkObjectResult && archivedProd.Status == ProductStatus.Archived && calcThrewArchived,
            "Enum: ArchiveProduct sets ProductStatus.Archived and ProductService.CalculateAsync refuses archived items");

        // 5) Phase 5 end-to-end checks: WorkTask.Status/Priority/Repeat enums
        var availability = scope.ServiceProvider.GetRequiredService<StaffAvailability>();
        var taskSvc = scope.ServiceProvider.GetRequiredService<TaskService>();
        var taskCtrl = new TaskController(taskSvc, rights);
        var orgCtrl = new OrganizationController(db, rights, NullLogger<OrganizationController>.Instance);
        var taskAdmin = SuperAdminPrincipal("admin@nuraherbex.com");
        await PrepareStaffControllerAsync(taskCtrl, taskAdmin, scope.ServiceProvider, "/api/admin/tasks");
        await PrepareStaffControllerAsync(orgCtrl, taskAdmin, scope.ServiceProvider, "/api/admin/departments");
        await PrepareStaffControllerAsync(staffCtrl, taskAdmin, scope.ServiceProvider, "/api/admin/employees");

        var opsDept = new Department { Name = "Operations", IsActive = true };
        var secondEmp = new Employee { Name = "Priya Tasks", Email = "priya.tasks@nuraherbex.com", Role = EmployeeRole.Sales, IsActive = true, PasswordHash = AdminPassword.Hash("Password123!") };
        db.Departments.Add(opsDept);
        db.Employees.Add(secondEmp);
        await db.SaveChangesAsync();

        // Preserve exact validation error behavior on uppercase/unknown strings & missing repeat due date
        var badTaskPri = await taskCtrl.Create(new TaskSaveRequest { Title = "Bad pri", Priority = "HIGH" });
        var badTaskRep = await taskCtrl.Create(new TaskSaveRequest { Title = "Bad rep", Repeat = "hourly" });
        var badTaskDue = await taskCtrl.Create(new TaskSaveRequest { Title = "No due repeat", Repeat = TaskRepeats.Weekly });
        check(badTaskPri is BadRequestObjectResult && badTaskRep is BadRequestObjectResult && badTaskDue is BadRequestObjectResult,
            "Enum: TaskController preserves exact validation rejections for casing, unknown repeat, and missing repeat DueAt");

        // Create tasks across priorities and repeating series
        var todayStart = FollowUpService.TodayStartUtc();
        var tomorrow = todayStart.AddDays(1);
        var dueTodayTarget = DateTime.UtcNow.AddMinutes(10) < tomorrow ? DateTime.UtcNow.AddMinutes(10) : tomorrow.AddSeconds(-5);
        var overdueRepDue = dueTodayTarget.AddDays(-7);
        var repRes = await taskCtrl.Create(new TaskSaveRequest
        {
            Title = "Dispatch daily packs",
            Priority = TaskPriorities.High,
            Repeat = TaskRepeats.Daily,
            DueAt = overdueRepDue,
            AssignedToEmployeeId = secondEmp.Id,
            DepartmentId = opsDept.Id,
            Checklist = ["Count cartons"],
        }) as ObjectResult;
        var repTaskId = ((repRes?.Value as TaskDetailResponse)?.Task?.Id) ?? 0;
        var repEntity = await db.WorkTasks.AsNoTracking().SingleAsync(t => t.Id == repTaskId);
        var urgentRes = await taskCtrl.Create(new TaskSaveRequest
        {
            Title = "Urgent escalation",
            Priority = TaskPriorities.Urgent,
            DueAt = DateTime.UtcNow.AddHours(-1),
            AssignedToEmployeeId = secondEmp.Id,
            DepartmentId = opsDept.Id,
        }) as ObjectResult;
        var urgentTaskId = ((urgentRes?.Value as TaskDetailResponse)?.Task?.Id) ?? 0;
        var upcomingRes = await taskCtrl.Create(new TaskSaveRequest
        {
            Title = "Future audit",
            Priority = TaskPriorities.Low,
            StartAt = todayStart.AddDays(3),
            DueAt = todayStart.AddDays(4),
            AssignedToEmployeeId = secondEmp.Id,
        }) as ObjectResult;
        var upcomingTaskId = ((upcomingRes?.Value as TaskDetailResponse)?.Task?.Id) ?? 0;
        var blockedRes = await taskCtrl.Create(new TaskSaveRequest
        {
            Title = "Waiting supplier",
            Priority = TaskPriorities.Normal,
            AssignedToEmployeeId = secondEmp.Id,
            DepartmentId = opsDept.Id,
        }) as ObjectResult;
        var blockedTaskId = ((blockedRes?.Value as TaskDetailResponse)?.Task?.Id) ?? 0;
        await taskCtrl.Status(blockedTaskId, new TaskStatusRequest { Status = TaskStatuses.Blocked });

        check(repRes?.StatusCode == 201 && repEntity.Status == WorkTaskStatus.Todo && (int)repEntity.Status == 1
              && repEntity.Priority == WorkTaskPriority.High && (int)repEntity.Priority == 3
              && repEntity.Repeat == WorkTaskRepeat.Daily && (int)repEntity.Repeat == 2,
            "Enum: Created WorkTask persists WorkTaskStatus.Todo (1), WorkTaskPriority.High (3), WorkTaskRepeat.Daily (2)");

        // Filtering & priority ordering in ListAsync
        var openList = await taskCtrl.List("open", secondEmp.Id.ToString(), null, null, null, null, null);
        var highFiltered = await taskCtrl.List("open", secondEmp.Id.ToString(), null, TaskPriorities.High, null, null, null);
        var badPriFiltered = await taskCtrl.List("open", null, null, "not_a_priority", null, null, null);
        var blockedFiltered = await taskCtrl.List("open", secondEmp.Id.ToString(), null, null, TaskStatuses.Blocked, null, null);
        var badStFiltered = await taskCtrl.List("open", null, null, null, "not_a_status", null, null);
        var upcomingList = await taskCtrl.List("upcoming", secondEmp.Id.ToString(), null, null, null, null, null);
        check(openList.Items.Count == 3
              && openList.Items[0].Id == urgentTaskId && openList.Items[1].Id == repTaskId && openList.Items[2].Id == blockedTaskId
              && highFiltered.Items.Count == 1 && highFiltered.Items[0].Id == repTaskId && highFiltered.Items[0].Priority == "high"
              && blockedFiltered.Items.Count == 1 && blockedFiltered.Items[0].Status == "blocked"
              && badPriFiltered.Items.Count == 0 && badStFiltered.Items.Count == 0
              && openList.Counts.Upcoming >= 1 && upcomingList.Items.Any(x => x.Id == upcomingTaskId && x.IsUpcoming),
            "Enum: TaskService.ListAsync preserves priority sort order, upcoming split, string filters, and 0-row unknown filter semantics");

        // Update audit text preserves lowercase wire codes ("urgent", "weekly")
        var updTaskRes = await taskCtrl.Update(repTaskId, new TaskSaveRequest
        {
            Title = "Dispatch daily packs",
            Priority = TaskPriorities.Urgent,
            Repeat = TaskRepeats.Weekly,
            DueAt = overdueRepDue,
            AssignedToEmployeeId = secondEmp.Id,
            DepartmentId = opsDept.Id,
        });
        var repAfterUpdate = (await taskCtrl.Get(repTaskId) as OkObjectResult)?.Value as TaskDetailResponse;
        check(updTaskRes is OkObjectResult
              && repAfterUpdate?.Task?.Priority == "urgent" && repAfterUpdate.Task.Repeat == "weekly"
              && repAfterUpdate.Activity.Any(a => a.Text == "Set the priority to urgent")
              && repAfterUpdate.Activity.Any(a => a.Text == "Set it to repeat weekly"),
            "Enum: Updating WorkTask preserves lowercase codes in Activity log and DTO");

        // Status transitions + checklist guard + label formatting + reopen
        var badCaseStatus = await taskCtrl.Status(repTaskId, new TaskStatusRequest { Status = "DONE" });
        var earlyDoneRes = await taskCtrl.Status(upcomingTaskId, new TaskStatusRequest { Status = TaskStatuses.Done });
        var untickedDoneRes = await taskCtrl.Status(repTaskId, new TaskStatusRequest { Status = TaskStatuses.Done });
        var moveInProgRes = await taskCtrl.Status(repTaskId, new TaskStatusRequest { Status = TaskStatuses.InProgress });
        var detailInProg = (await taskCtrl.Get(repTaskId) as OkObjectResult)?.Value as TaskDetailResponse;
        check(badCaseStatus is BadRequestObjectResult && earlyDoneRes is BadRequestObjectResult && untickedDoneRes is BadRequestObjectResult
              && moveInProgRes is OkObjectResult && detailInProg?.Task?.Status == "in_progress"
              && detailInProg.Activity.Any(a => a.Text == "Moved from To do to In progress"),
            "Enum: SetStatusAsync enforces lowercase statuses, upcoming/checklist guards, and readable Label transitions");

        var stepId = detailInProg!.Checklist[0].Id;
        await taskCtrl.TickItem(repTaskId, stepId, new TaskChecklistRequest { Done = true });
        var doneRes = await taskCtrl.Status(repTaskId, new TaskStatusRequest { Status = TaskStatuses.Done });
        var doneEntity = await db.WorkTasks.AsNoTracking().SingleAsync(t => t.Id == repTaskId);
        var doneActs = await db.TaskActivities.AsNoTracking().Where(a => a.TaskId == repTaskId).ToListAsync();
        check(doneRes is OkObjectResult && doneEntity.Status == WorkTaskStatus.Done && doneEntity.CompletedAt is not null
              && doneActs.Any(a => a.Text.StartsWith("Next weekly task will be created on ")),
            "Enum: Completing WorkTask sets Status=Done (4) and logs lowercase repeat interval");

        // Reopen Done task & verify next repeat creation via RunCycleAsync on a fresh daily task
        var reopenRes = await taskCtrl.Status(repTaskId, new TaskStatusRequest { Status = TaskStatuses.Todo }) as OkObjectResult;
        var reopenedEntity = await db.WorkTasks.AsNoTracking().SingleAsync(t => t.Id == repTaskId);
        check((reopenRes?.Value as ApiResult)?.Message == "Task reopened. It is back in your open tasks."
              && reopenedEntity.Status == WorkTaskStatus.Todo && reopenedEntity.CompletedAt is null,
            "Enum: Reopening WorkTask transitions Status back to Todo (1) cleanly");

        // Keep one completed task (to check exclusions) and trigger RunCycleAsync for repeat + overdue
        await taskCtrl.Status(repTaskId, new TaskStatusRequest { Status = TaskStatuses.Done });
        await taskSvc.RunCycleAsync();
        var nextRepeatCopy = await db.WorkTasks.AsNoTracking()
            .Where(t => t.Id != repTaskId && t.Title == "Dispatch daily packs" && t.Status == WorkTaskStatus.Todo)
            .SingleOrDefaultAsync();
        var nextCopyActs = nextRepeatCopy is null ? [] : await db.TaskActivities.AsNoTracking().Where(a => a.TaskId == nextRepeatCopy.Id).ToListAsync();
        var urgentAfterCycle = await db.WorkTasks.AsNoTracking().SingleAsync(t => t.Id == urgentTaskId);
        check(nextRepeatCopy is not null
              && nextRepeatCopy.Priority == WorkTaskPriority.Urgent && nextRepeatCopy.Repeat == WorkTaskRepeat.Weekly
              && nextCopyActs.Any(a => a.Text == "Created automatically on its weekly repeat day"),
            "Enum: TaskService.RunCycleAsync creates repeating copy with enums and lowercase repeat activity");
        check(urgentAfterCycle.OverdueNotifiedAt is not null && doneEntity.OverdueNotifiedAt is null,
            "Enum: TaskService.RunCycleAsync marks open overdue WorkTask and skips Done tasks");

        // Caller integrations: OrganizationController.Departments, StaffAvailability.WorkloadAsync, StaffController.Employees & RemoveEmployee, ReportService.TasksAsync
        var deptList = await orgCtrl.Departments();
        var opsDto = deptList.Departments.Single(d => d.Id == opsDept.Id);
        var empListRes = await staffCtrl.Employees();
        var priyaSummary = empListRes.Employees.Single(e => e.Id == secondEmp.Id);
        var workload = await availability.WorkloadAsync();
        // Open tasks for secondEmp in Ops & overall: urgent (Todo), blocked (Blocked), future audit (Todo), nextRepeatCopy (Todo); repTaskId is Done!
        var expectedOpenForPriya = await db.WorkTasks.CountAsync(t => t.AssignedToEmployeeId == secondEmp.Id && t.Status != WorkTaskStatus.Done);
        var expectedOpsOpen = await db.WorkTasks.CountAsync(t => t.DepartmentId == opsDept.Id && t.Status != WorkTaskStatus.Done);
        check(expectedOpenForPriya == 4 && expectedOpsOpen == 3
              && opsDto.OpenTasks == expectedOpsOpen && priyaSummary.OpenTasks == expectedOpenForPriya
              && workload.GetValueOrDefault(secondEmp.Id) == expectedOpenForPriya,
            "Enum: OrganizationController, StaffController, and StaffAvailability exclude WorkTaskStatus.Done from OpenTasks/Workload");

        var taskReport = await reportSvc.BuildAsync("tasks", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), "Today", null);
        check(taskReport is not null
              && taskReport.Rows.Any(r => r[5] == "normal" && r[6] == "blocked")
              && taskReport.Rows.Any(r => r[5] == "urgent" && r[6] == "done")
              && taskReport.Summary.First(s => s.Label == "Blocked").Value == "1"
              && taskReport.Summary.First(s => s.Label == "Done").Value.StartsWith("1 (")
              && taskReport.Summary.First(s => s.Label == "Urgent or high").Value == "3",
            "Enum: ReportService.TasksAsync emits lowercase Priority/Status codes and accurate enum summary counts");

        await staffCtrl.RemoveEmployee(secondEmp.Id, "unassigned");
        var doneStillAssigned = await db.WorkTasks.AsNoTracking().SingleAsync(t => t.Id == repTaskId);
        var openAfterRemoval = await db.WorkTasks.AsNoTracking().Where(t => t.Id == urgentTaskId || t.Id == blockedTaskId).ToListAsync();
        check(doneStillAssigned.AssignedToEmployeeId == secondEmp.Id
              && openAfterRemoval.All(t => t.AssignedToEmployeeId == null),
            "Enum: StaffController.RemoveEmployee unassigns open tasks while keeping Done tasks intact");
        }
        finally
        {
            FollowUpService.Clock = previousClock;
        }
    }
}
