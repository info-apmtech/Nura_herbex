using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

const string password = "Test-only-admin-password!";
var salt = RandomNumberGenerator.GetBytes(32);
var hash = "pbkdf2-sha512:210000:" + Convert.ToBase64String(salt) + ":" +
    Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA512, 64));
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
Check(AdminPassword.Verify(password, hash), "Password hash accepts the correct password");
Check(!AdminPassword.Verify("incorrect", hash), "Password hash rejects an incorrect password");
foreach (var malformed in new[] { "bad", "pbkdf2-sha512:210000:invalid:invalid", "pbkdf2-sha512:2147483647:a:b" })
    Check(!AdminPassword.Verify(password, malformed), "Malformed password hash fails closed");

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
var apiDll = Path.Combine(root, "src/Nuraherbex.Api/bin", configuration, "net10.0/Nuraherbex.Api.dll");
var contentRoot = Path.Combine(Path.GetTempPath(), "nura-admin-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(contentRoot);
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
var jwt = new JwtOptions { Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)) };
var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var arg in new[] { apiDll, "--contentRoot", contentRoot, "--urls", $"http://127.0.0.1:{port}",
    "--Database:Provider", "InMemory", "--Database:AutoCreate", "true", "--Jwt:Secret", jwt.Secret,
    "--Admin:Email", "admin@example.test", "--Admin:PasswordHash", hash }) start.ArgumentList.Add(arg);
start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
using var process = Process.Start(start) ?? throw new Exception("API failed to start");
var stdout = process.StandardOutput.ReadToEndAsync();
var stderr = process.StandardError.ReadToEndAsync();
try
{
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
    var ready = false;
    var startupDeadline = DateTime.UtcNow.AddMinutes(2);
    while (DateTime.UtcNow < startupDeadline)
    {
        if (process.HasExited) throw new Exception("API exited: " + await stderr + await stdout);
        try { using var response = await http.GetAsync("/api/admin/verify"); ready = true; break; }
        catch (HttpRequestException) { await Task.Delay(250); }
        catch (TaskCanceledException) { await Task.Delay(250); }
    }
    Check(ready, "Isolated API starts with an in-memory database");
    foreach (var path in new[] { "verify", "orders", "customers", "products", "batches", "payments", "catalog/products", "catalog/coupons" })
    {
        using var response = await http.GetAsync("/api/admin/" + path);
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous access rejected: " + path);
    }
    using (var bad = await http.PostAsJsonAsync("/api/admin/login", new { Email = "admin@example.test", Password = "wrong" }))
        Check(bad.StatusCode == HttpStatusCode.Unauthorized, "Incorrect login rejected");
    using (var badEmail = await http.PostAsJsonAsync("/api/admin/login", new { Email = "other@example.test", Password = password }))
        Check(badEmail.StatusCode == HttpStatusCode.Unauthorized, "Incorrect email rejected");
    foreach (var action in new[] { "ready", "book", "label", "cancel" })
    {
        using var denied = await http.PostAsJsonAsync($"/api/shipping/unknown/{action}", new { });
        Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Anonymous shipping action rejected: " + action);
    }
    using (var denied = await http.GetAsync("/api/shipping/unknown/track"))
        Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Anonymous shipping tracking rejected");
    using (var closed = await http.PostAsJsonAsync("/api/webhooks/shadowfax", new { awb_number = "unknown", status_id = "delivered" }))
        Check(closed.StatusCode == HttpStatusCode.ServiceUnavailable, "Unconfigured webhook fails closed over HTTP");
    using (var forged = await http.PostAsJsonAsync("/api/orders/unknown/confirm", new { udf1 = "different" }))
        Check(forged.StatusCode == HttpStatusCode.BadRequest, "Order confirmation rejects mismatched signed reference");

    using var login = await http.PostAsJsonAsync("/api/admin/login", new { Email = " ADMIN@example.test ", Password = password });
    var result = await login.Content.ReadFromJsonAsync<AdminLoginResponse>();
    Check(login.IsSuccessStatusCode && result?.User?.Role == "admin" && !string.IsNullOrEmpty(result.Token), "Admin login returns an admin token and normalizes email");
    http.DefaultRequestHeaders.Authorization = new("Bearer", result!.Token);
    foreach (var path in new[] { "verify", "orders", "customers", "products" })
    {
        using var response = await http.GetAsync("/api/admin/" + path);
        Check(response.IsSuccessStatusCode, "Admin access granted: " + path);
    }
    var draft = new AdminProductDto { Name = "Test botanical mix", Sku = "TEST-MIX", Price = 100, CompareAtPrice = 150, CostPrice = 40, StockQuantity = 5, WeightKg = .2m, LengthCm = 10, BreadthCm = 10, HeightCm = 10, Status = "draft", Category = "Nutrition" };
    using var createdResponse = await http.PostAsJsonAsync("/api/admin/catalog/products", draft);
    var created = await createdResponse.Content.ReadFromJsonAsync<AdminProductResponse>();
    Check(createdResponse.StatusCode == HttpStatusCode.Created && created?.Product is not null, "Admin creates a draft product");
    var product = created!.Product!;
    var publicCatalog = await http.GetFromJsonAsync<ProductListResponse>("/api/products");
    Check(!publicCatalog!.Products.Any(p => p.Id == product.Id), "Draft product is hidden from the shop");
    using (var duplicate = await http.PostAsJsonAsync("/api/admin/catalog/products", draft)) Check(duplicate.StatusCode == HttpStatusCode.Conflict, "Duplicate product SKU rejected");
    product.Status = "active";
    var productUrl = "/api/admin/catalog/products/" + product.Id;
    using var update = await http.PutAsJsonAsync(productUrl, product);
    var updated = await update.Content.ReadFromJsonAsync<AdminProductResponse>();
    Check(update.IsSuccessStatusCode, "Admin publishes a draft product");
    using (var stale = await http.PutAsJsonAsync(productUrl, product)) Check(stale.StatusCode == HttpStatusCode.Conflict, "Stale product edit rejected");
    product = updated!.Product!;
    publicCatalog = await http.GetFromJsonAsync<ProductListResponse>("/api/products");
    Check(publicCatalog!.Products.Any(p => p.Id == product.Id && p.Price == 100), "Published product appears in the shop with its saved price");
    var invalidProduct = new AdminProductDto { Name = "Bad", Sku = "INVALID", Price = -1 };
    using (var bad = await http.PostAsJsonAsync("/api/admin/catalog/products", invalidProduct)) Check(bad.StatusCode == HttpStatusCode.BadRequest, "Invalid product data rejected");
    var cart = new ValidateCartRequest { Items = new() { new CartLineDto { Id = product.Id, Quantity = 1 } } };
    using (var pricing = await http.PostAsJsonAsync("/api/orders/validate", cart))
    {
        var totals = await pricing.Content.ReadFromJsonAsync<OrderTotalsDto>();
        Check(pricing.IsSuccessStatusCode && totals!.Subtotal == 100, "Checkout uses the managed product price");
    }
    var duplicates = new ValidateCartRequest { Items = new() { new CartLineDto { Id = product.Id, Quantity = 3 }, new CartLineDto { Id = product.Id, Quantity = 3 } } };
    using (var stock = await http.PostAsJsonAsync("/api/orders/validate", duplicates)) Check(stock.StatusCode == HttpStatusCode.BadRequest, "Duplicate cart lines cannot bypass stock limits");
    var coupon = new CouponDto { Code = "test25", DiscountType = "PERCENTAGE", DiscountValue = 25, MaxDiscountCap = 10, MinOrderAmount = 50 };
    using (var c = await http.PostAsJsonAsync("/api/admin/catalog/coupons", coupon)) Check(c.StatusCode == HttpStatusCode.Created, "Admin creates a coupon");
    using (var c = await http.PostAsJsonAsync("/api/admin/catalog/coupons", coupon)) Check(c.StatusCode == HttpStatusCode.Conflict, "Duplicate coupon code rejected regardless of input case");
    cart.CouponCode = "test25";
    using (var pricing = await http.PostAsJsonAsync("/api/orders/validate", cart))
    {
        var totals = await pricing.Content.ReadFromJsonAsync<OrderTotalsDto>();
        Check(pricing.IsSuccessStatusCode && totals!.DiscountAmount == 10 && totals.CouponCode == "TEST25", "Percentage coupon respects its discount cap and normalizes the code");
    }
    coupon.Code = "TEST25"; coupon.MinOrderAmount = 200;
    using (var save = await http.PutAsJsonAsync("/api/admin/catalog/coupons/TEST25", coupon)) Check(save.IsSuccessStatusCode, "Admin updates coupon requirements");
    using (var pricing = await http.PostAsJsonAsync("/api/orders/validate", cart)) Check(pricing.StatusCode == HttpStatusCode.BadRequest, "Coupon minimum order enforced");
    coupon.MinOrderAmount = 0; coupon.DiscountType = "FIXED"; coupon.DiscountValue = 500;
    using (var save = await http.PutAsJsonAsync("/api/admin/catalog/coupons/TEST25", coupon)) Check(save.IsSuccessStatusCode, "Admin changes a coupon to a fixed amount");
    using (var pricing = await http.PostAsJsonAsync("/api/orders/validate", cart))
    {
        var totals = await pricing.Content.ReadFromJsonAsync<OrderTotalsDto>();
        Check(pricing.IsSuccessStatusCode && totals!.DiscountAmount == 100 && totals.FinalTotal == totals.ShippingFee, "Fixed discount cannot exceed subtotal or consume shipping");
    }
    coupon.ExpiresAt = DateTime.UtcNow.AddDays(-1);
    using (var save = await http.PutAsJsonAsync("/api/admin/catalog/coupons/TEST25", coupon)) Check(save.IsSuccessStatusCode, "Coupon expiry saved");
    using (var pricing = await http.PostAsJsonAsync("/api/orders/validate", cart)) Check(pricing.StatusCode == HttpStatusCode.BadRequest, "Expired coupon rejected at checkout");
    coupon.ExpiresAt = null;
    using (var save = await http.PutAsJsonAsync("/api/admin/catalog/coupons/TEST25", coupon)) Check(save.IsSuccessStatusCode, "Coupon expiry can be removed");
    using (var disable = await http.DeleteAsync("/api/admin/catalog/coupons/TEST25")) Check(disable.IsSuccessStatusCode, "Admin disables coupon");
    using (var pricing = await http.PostAsJsonAsync("/api/orders/validate", cart)) Check(pricing.StatusCode == HttpStatusCode.BadRequest, "Disabled coupon rejected at checkout");
    using (var invalidCoupon = await http.PostAsJsonAsync("/api/admin/catalog/coupons", new CouponDto { Code = "BAD", DiscountValue = 101 })) Check(invalidCoupon.StatusCode == HttpStatusCode.BadRequest, "Percentage discount over 100 rejected");
    using (var archive = await http.DeleteAsync(productUrl)) Check(archive.IsSuccessStatusCode, "Admin archives product");
    publicCatalog = await http.GetFromJsonAsync<ProductListResponse>("/api/products");
    Check(!publicCatalog!.Products.Any(p => p.Id == product.Id), "Archived product removed from the shop");
    cart.CouponCode = null;
    using (var pricing = await http.PostAsJsonAsync("/api/orders/validate", cart)) Check(pricing.StatusCode == HttpStatusCode.BadRequest, "Archived product rejected even when already in a cart");
    var managedProducts = await http.GetFromJsonAsync<AdminProductListResponse>("/api/admin/catalog/products");
    Check(managedProducts!.Products.Any(p => p.Id == product.Id && p.Status == "archived"), "Archived product remains available in admin history");
    using (var register = await http.PostAsJsonAsync("/api/auth/register", new RegisterRequest { FullName = "Test Customer", Email = "profile@example.test", Phone = "9999988888", Password = "TestCustomerPassword!" }))
    {
        var account = await register.Content.ReadFromJsonAsync<AuthResponse>();
        Check(register.IsSuccessStatusCode && account?.Customer is not null, "Test customer account created");
        using var profile = await http.PutAsJsonAsync("/api/admin/customers/" + account!.Customer!.Id, new UpdateProfileRequest { FullName = "Updated Customer", Phone = "9999977777", ShippingAddress = new AddressDto { AddressLine1 = "Test address", City = "Bengaluru", State = "Karnataka", Pincode = "560001" } });
        var customer = await profile.Content.ReadFromJsonAsync<CustomerResponse>();
        Check(profile.IsSuccessStatusCode && customer?.Customer?.FullName == "Updated Customer", "Admin updates customer profile and address");
    }
    var customerToken = new TokenService(Options.Create(jwt)).CreateCustomerToken(new Customer { Id = "test", Email = "customer@example.test", FullName = "Test Customer" });
    http.DefaultRequestHeaders.Authorization = new("Bearer", customerToken);
    foreach (var path in new[] { "verify", "orders", "customers", "products", "batches", "payments", "catalog/products", "catalog/coupons" })
    {
        using var response = await http.GetAsync("/api/admin/" + path);
        Check(response.StatusCode == HttpStatusCode.Forbidden, "Customer access rejected: " + path);
    }
    using (var forbiddenWrite = await http.PostAsJsonAsync("/api/admin/catalog/products", draft)) Check(forbiddenWrite.StatusCode == HttpStatusCode.Forbidden, "Customer cannot create products");
    using (var forbiddenCoupon = await http.DeleteAsync("/api/admin/catalog/coupons/TEST25")) Check(forbiddenCoupon.StatusCode == HttpStatusCode.Forbidden, "Customer cannot disable coupons");
    http.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-token");
    using var invalid = await http.GetAsync("/api/admin/verify");
    Check(invalid.StatusCode == HttpStatusCode.Unauthorized, "Invalid token rejected");
}
finally
{
    if (!process.HasExited) process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync();
    Directory.Delete(contentRoot, recursive: false);
}
