using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---- Options ---------------------------------------------------------------------------
builder.Services.Configure<StoreOptions>(cfg.GetSection(StoreOptions.Section));
builder.Services.Configure<JwtOptions>(cfg.GetSection(JwtOptions.Section));
builder.Services.Configure<AdminOptions>(cfg.GetSection(AdminOptions.Section));
builder.Services.Configure<PayUOptions>(cfg.GetSection(PayUOptions.Section));
builder.Services.Configure<ShiprocketOptions>(cfg.GetSection(ShiprocketOptions.Section));
builder.Services.Configure<EmailOptions>(cfg.GetSection(EmailOptions.Section));
builder.Services.Configure<WhatsAppOptions>(cfg.GetSection(WhatsAppOptions.Section));

// ---- Database --------------------------------------------------------------------------
// Database:Provider = SqlServer (default) | InMemory (local demo without a database)
var provider = cfg["Database:Provider"] ?? "SqlServer";
builder.Services.AddDbContext<NuraDbContext>(o =>
{
    if (provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase)) o.UseInMemoryDatabase("nuraherbex");
    else o.UseSqlServer(cfg.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured."),
        sql => sql.EnableRetryOnFailure(3));
});

// ---- Services --------------------------------------------------------------------------
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ProductService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<PayUService>();
builder.Services.AddScoped<CustomerAuthService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddHttpClient<EmailService>();
builder.Services.AddHttpClient<ShiprocketService>();
builder.Services.AddHttpClient<WhatsAppService>();

// ---- Auth (JWT for customers and admins) ----------------------------------------------
var jwt = cfg.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Secret) || jwt.Secret.Length < 32)
    throw new InvalidOperationException("Jwt:Secret must be configured with at least 32 characters (use user-secrets or environment variables).");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer,
        ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1),
        ValidateIssuerSigningKey = true, IssuerSigningKey = TokenService.Key(jwt.Secret),
        RoleClaimType = ClaimTypes.Role, NameClaimType = ClaimTypes.Name,
    };
    // Accept our raw claim names (we write ClaimTypes.* long names into the token).
    o.Events = new JwtBearerEvents();
});
builder.Services.AddAuthorization();

// ---- Rate limiting ---------------------------------------------------------------------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
        await ctx.HttpContext.Response.WriteAsJsonAsync(new ApiResult { Success = false, Message = "Too many requests. Please try again in a minute." }, ct);
    o.AddPolicy("orders", http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("admin-login", http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1) }));
});

// ---- CORS (Blazor WebAssembly origin) --------------------------------------------------
var origins = cfg.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (origins.Length == 0) p.AllowAnyOrigin(); else p.WithOrigins(origins).AllowCredentials();
    p.AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear(); o.KnownProxies.Clear();
});

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

// ---- Schema bootstrap ------------------------------------------------------------------
// `dotnet run -- --generate-sql <file>` writes the CREATE script (for DBAs / hosts without create rights).
var genIdx = Array.IndexOf(args, "--generate-sql");
if (genIdx >= 0)
{
    using var scope = app.Services.CreateScope();
    var script = scope.ServiceProvider.GetRequiredService<NuraDbContext>().Database.GenerateCreateScript();
    await File.WriteAllTextAsync(args.ElementAtOrDefault(genIdx + 1) ?? "schema.sql", script);
    Console.WriteLine("Schema script written.");
    return;
}

if (cfg.GetValue("Database:AutoCreate", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<NuraDbContext>();
    try
    {
        await db.Database.EnsureCreatedAsync();
        await DbSeeder.SeedAsync(db, cfg.GetValue("Database:SeedDemoBatch", false));
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Database initialisation failed. Check ConnectionStrings:Default, or set Database:Provider=InMemory for a local demo.");
        if (!app.Environment.IsDevelopment()) throw;
    }
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/", () => Results.Redirect("/api/health"));

app.Run();
