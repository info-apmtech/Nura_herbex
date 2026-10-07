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
    foreach (var path in new[] { "verify", "orders", "customers", "products", "batches", "payments" })
    {
        using var response = await http.GetAsync("/api/admin/" + path);
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous access rejected: " + path);
    }
    using (var bad = await http.PostAsJsonAsync("/api/admin/login", new { Email = "admin@example.test", Password = "wrong" }))
        Check(bad.StatusCode == HttpStatusCode.Unauthorized, "Incorrect login rejected");
    using (var badEmail = await http.PostAsJsonAsync("/api/admin/login", new { Email = "other@example.test", Password = password }))
        Check(badEmail.StatusCode == HttpStatusCode.Unauthorized, "Incorrect email rejected");
    using var login = await http.PostAsJsonAsync("/api/admin/login", new { Email = " ADMIN@example.test ", Password = password });
    var result = await login.Content.ReadFromJsonAsync<AdminLoginResponse>();
    Check(login.IsSuccessStatusCode && result?.User?.Role == "admin" && !string.IsNullOrEmpty(result.Token), "Admin login returns an admin token and normalizes email");
    http.DefaultRequestHeaders.Authorization = new("Bearer", result!.Token);
    foreach (var path in new[] { "verify", "orders", "customers", "products" })
    {
        using var response = await http.GetAsync("/api/admin/" + path);
        Check(response.IsSuccessStatusCode, "Admin access granted: " + path);
    }
    var customerToken = new TokenService(Options.Create(jwt)).CreateCustomerToken(new Customer { Id = "test", Email = "customer@example.test", FullName = "Test Customer" });
    http.DefaultRequestHeaders.Authorization = new("Bearer", customerToken);
    foreach (var path in new[] { "verify", "orders", "customers", "products", "batches", "payments" })
    {
        using var response = await http.GetAsync("/api/admin/" + path);
        Check(response.StatusCode == HttpStatusCode.Forbidden, "Customer access rejected: " + path);
    }
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
