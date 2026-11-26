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
// Machine-local settings (PayU keys etc.), git-ignored. Loaded last so it always applies, however the API is launched.
cfg.AddJsonFile(Path.Combine(builder.Environment.ContentRootPath, "appsettings.Local.json"), optional: true, reloadOnChange: true);

cfg.AddEnvironmentVariables();
cfg.AddCommandLine(args);

// ---- Options ---------------------------------------------------------------------------
builder.Services.Configure<StoreOptions>(cfg.GetSection(StoreOptions.Section));
builder.Services.Configure<JwtOptions>(cfg.GetSection(JwtOptions.Section));
builder.Services.Configure<AdminOptions>(cfg.GetSection(AdminOptions.Section));
builder.Services.AddOptions<PayUOptions>().Bind(cfg.GetSection(PayUOptions.Section))
    .Validate(o => builder.Environment.IsDevelopment() || !o.AllowSimulation, "PayU simulation is permitted only in Development.").ValidateOnStart();
builder.Services.Configure<ShiprocketOptions>(cfg.GetSection(ShiprocketOptions.Section));
builder.Services.AddOptions<ShadowfaxOptions>().Bind(cfg.GetSection(ShadowfaxOptions.Section))
    .Validate(o => o.Env is "staging" or "production", "Shadowfax:Env must be staging or production.")
    .Validate(o => o.OrderType is "marketplace" or "warehouse", "Unsupported Shadowfax order type.")
    .Validate(o => o.ServiceTier is "Regular" or "Surface", "Unsupported Shadowfax service tier.")
    .ValidateOnStart();
builder.Services.Configure<ShippingOptions>(cfg.GetSection(ShippingOptions.Section));
builder.Services.Configure<EmailOptions>(cfg.GetSection(EmailOptions.Section));
builder.Services.Configure<WhatsAppOptions>(cfg.GetSection(WhatsAppOptions.Section));

// ---- Database & Multi-Project Configuration --------------------------------------------
var projectName = cfg["ProjectName"] ?? ProjectConfig.NeuraHerbex;
var provider = cfg["Database:Provider"] ?? "SqlServer";
var connectionString = cfg.GetConnectionString(projectName)
                       ?? cfg.GetConnectionString("Default")
                       ?? throw new InvalidOperationException($"ConnectionStrings:{projectName} or Default is not configured.");

builder.Services.AddDbContext<NuraDbContext>(o =>
{
    if (provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        o.UseInMemoryDatabase(projectName.ToLowerInvariant());
    else
        o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3));
});

// ---- Services --------------------------------------------------------------------------
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ProductService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<ConsultationService>();
builder.Services.AddScoped<OrderOperationLock>();
builder.Services.AddScoped<ShadowfaxTrackingProcessor>();
builder.Services.AddHostedService<ShadowfaxReconciliationWorker>();
builder.Services.AddHostedService<ShiprocketTrackingWorker>();
builder.Services.AddScoped<PayUService>();
builder.Services.AddScoped<CustomerAuthService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddHttpClient<EmailService>();
builder.Services.AddHttpClient<ShiprocketService>();
builder.Services.AddHttpClient<ShadowfaxService>(http => http.Timeout = TimeSpan.FromSeconds(25));
builder.Services.AddSingleton<ShippingSettingsStore>();
builder.Services.AddScoped<ShippingGateway>();
builder.Services.AddSingleton<MediaSigner>();
builder.Services.AddSingleton<InvoiceService>();
builder.Services.AddScoped<StaffAvailability>();
builder.Services.AddScoped<MessageTemplateService>();
builder.Services.AddScoped<LibraryService>();
builder.Services.AddScoped<LeadService>();
builder.Services.Configure<FollowUpOptions>(cfg.GetSection(FollowUpOptions.Section));
builder.Services.AddScoped<FollowUpService>();
builder.Services.AddScoped<CrmDashboardService>();
builder.Services.AddScoped<RightsService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<ReportService>();
builder.Services.Configure<AiOptions>(o => { cfg.GetSection(AiOptions.Section).Bind(o); if (string.IsNullOrWhiteSpace(o.ApiKey)) o.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? ""; });
builder.Services.AddScoped<ReplyAssistantService>();
builder.Services.AddHostedService<FollowUpWorker>();
builder.Services.AddHostedService<LeadAssignmentWorker>();
builder.Services.Configure<MetaAdsOptions>(cfg.GetSection(MetaAdsOptions.Section));
builder.Services.AddHttpClient<MetaAdsService>(h => h.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddScoped<AdsReportService>();
builder.Services.AddHostedService<MetaAdsWorker>();
builder.Services.Configure<GoogleDriveOptions>(cfg.GetSection(GoogleDriveOptions.Section));
builder.Services.AddHttpClient<GoogleDriveService>(h => h.Timeout = TimeSpan.FromSeconds(25));
builder.Services.AddScoped<BulkMessageService>();
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

// ---- Swagger (API explorer at /swagger) ------------------------------------------------
// Enabled in Development, or anywhere with Swagger:Enabled=true. Use the Authorize button for admin / customer JWTs.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo { Title = "Nura Herbex API", Version = "v1", Description = "Store, orders, PayU payments, admin, Shadowfax shipping, legacy Shiprocket tracking, and WhatsApp webhooks." });
    o.CustomSchemaIds(t => t.FullName?.Replace('+', '.'));
    o.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        Description = "Paste the token returned by POST /api/admin/login (admin) or /api/auth/login (customer). No 'Bearer ' prefix needed.",
    });
    // The WhatsApp event POST reads the raw body (needed for signature checks), so describe it manually for "Try it out".
    o.OperationFilter<RawJsonBodyOperationFilter>();
    // Lock icon / Authorize requirement only on endpoints that really need a login ([Authorize] and not [AllowAnonymous]).
    o.OperationFilter<AuthorizeOperationFilter>();
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

// Startup summary so it is obvious which database / PayU mode this process really uses (no secrets printed).
{
// Shadowfax settings problems must not stop the whole API (store, payments, WhatsApp): report them, and booking fails with a clear message instead.
{
    var sf = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ShadowfaxOptions>>().Value;
    var missing = new List<string>();
    if (!sf.IsConfigured) missing.Add(sf.IsProduction ? "Shadowfax:Token" : "Shadowfax:StagingToken");
    if (!sf.Pickup.IsComplete) missing.Add("Shadowfax:Pickup (AddressLine1, City, Pincode)");
    if (sf.WebhookToken.Length < 32) missing.Add("Shadowfax:WebhookToken (32+ characters)");
    if (missing.Count > 0)
        app.Logger.LogWarning("[Startup] Shadowfax ({Env}) is not fully configured - missing: {Missing}. Shipment booking and callbacks will fail until set (env vars such as Shadowfax__Token, or appsettings.Local.json).", sf.Env, string.Join(", ", missing));
}

    var csb = provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase) ? null : new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
    var payuKey = cfg["PayU:MerchantKey"];
    app.Logger.LogInformation("[Startup] Environment={Env} | Database={Provider} {Server}/{Db} | PayU={Payu} ({PayuEnv}) | UserSecrets={Secrets}",
        app.Environment.EnvironmentName, provider, csb?.DataSource ?? "-", csb?.InitialCatalog ?? "-",
        string.IsNullOrWhiteSpace(payuKey) ? "NOT CONFIGURED" : $"key {payuKey[..Math.Min(2, payuKey.Length)]}***", cfg["PayU:Env"] ?? "test",
        app.Environment.IsDevelopment() ? "enabled" : "off (not Development)");
}

if (cfg.GetValue("Database:AutoCreate", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<NuraDbContext>();
    try
    {
        // SQL Server: apply EF migrations (creates/updates tables). InMemory has no schema, so just ensure it exists.
        if (provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase)) await db.Database.EnsureCreatedAsync();
        else
        {
            try { await db.Database.MigrateAsync(); }
            catch (Exception mex) { app.Logger.LogWarning(mex, "EF Migration notice on startup: {Message}", mex.Message); }
            await EnsureCrmSchemaAsync(db, app.Logger);
        }
        await DbSeeder.SeedAsync(db, cfg.GetValue("Database:SeedDemoBatch", false));
        await WhatsAppMediaBackfill.RunAsync(db, app.Logger);
        await OrgSeeder.RunAsync(db, app.Logger);
        await scope.ServiceProvider.GetRequiredService<LibraryService>().SeedSamplesAsync();
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
app.UseMiddleware<PageRightsMiddleware>(); // page rights for every staff API route (closed by default)
app.UseAuthorization();
app.MapControllers();
var swaggerOn = app.Environment.IsDevelopment() || cfg.GetValue("Swagger:Enabled", false);
if (swaggerOn)
{
    app.UseSwagger();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/swagger/v1/swagger.json", "Nura Herbex API v1");
        o.RoutePrefix = "swagger";
        o.DocumentTitle = "Nura Herbex API";
        o.EnableTryItOutByDefault();
        o.DisplayRequestDuration();
    });
}
app.MapGet("/", () => Results.Redirect(swaggerOn ? "/swagger" : "/api/health"));

// Courier choice/credentials saved from the admin (Shipping setup) override appsettings; load them once the database is ready.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    try { await app.Services.GetRequiredService<ShippingSettingsStore>().LoadAsync(app.Services); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "[Shipping] Saved courier settings could not be loaded; using appsettings."); }
}));

static async Task EnsureCrmSchemaAsync(NuraDbContext db, ILogger log)
{
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('orders') AND name = 'OfflineDispatchNote')
                ALTER TABLE [orders] ADD [OfflineDispatchNote] nvarchar(500) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('orders') AND name = 'ShippingMode')
                ALTER TABLE [orders] ADD [ShippingMode] int NOT NULL DEFAULT 1;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('orders') AND name = 'AdvanceAmount')
                ALTER TABLE [orders] ADD [AdvanceAmount] decimal(18,2) NOT NULL DEFAULT 0;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'Age')
                ALTER TABLE [leads] ADD [Age] int NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'AssessmentUpdatedAt')
                ALTER TABLE [leads] ADD [AssessmentUpdatedAt] datetime2 NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'Gender')
                ALTER TABLE [leads] ADD [Gender] nvarchar(32) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'HasAssessment')
                ALTER TABLE [leads] ADD [HasAssessment] bit NOT NULL DEFAULT 0;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'HealthConcerns')
                ALTER TABLE [leads] ADD [HealthConcerns] nvarchar(max) NOT NULL DEFAULT '[]';

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'IsDrinking')
                ALTER TABLE [leads] ADD [IsDrinking] bit NOT NULL DEFAULT 0;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'IsSmoking')
                ALTER TABLE [leads] ADD [IsSmoking] bit NOT NULL DEFAULT 0;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'LastNoAnswerAt')
                ALTER TABLE [leads] ADD [LastNoAnswerAt] datetime2 NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'MaritalStatus')
                ALTER TABLE [leads] ADD [MaritalStatus] nvarchar(32) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'NoAnswerAttempts')
                ALTER TABLE [leads] ADD [NoAnswerAttempts] int NOT NULL DEFAULT 0;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'NoAnswerStartedAt')
                ALTER TABLE [leads] ADD [NoAnswerStartedAt] datetime2 NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'OtherHealthConcern')
                ALTER TABLE [leads] ADD [OtherHealthConcern] nvarchar(500) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'Priority')
                ALTER TABLE [leads] ADD [Priority] int NOT NULL DEFAULT 1;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'WorkType')
                ALTER TABLE [leads] ADD [WorkType] nvarchar(64) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'ProblemDuration')
                ALTER TABLE [leads] ADD [ProblemDuration] nvarchar(100) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'PreviousSupplements')
                ALTER TABLE [leads] ADD [PreviousSupplements] nvarchar(32) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'AssessmentNotes')
                ALTER TABLE [leads] ADD [AssessmentNotes] nvarchar(2000) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'PrimaryConcerns')
                ALTER TABLE [leads] ADD [PrimaryConcerns] nvarchar(max) NOT NULL DEFAULT '[]';

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'HealthConditions')
                ALTER TABLE [leads] ADD [HealthConditions] nvarchar(max) NOT NULL DEFAULT '[]';

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'PendingTransferToEmployeeId')
                ALTER TABLE [leads] ADD [PendingTransferToEmployeeId] bigint NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'PendingTransferFromEmployeeId')
                ALTER TABLE [leads] ADD [PendingTransferFromEmployeeId] bigint NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'PendingTransferRequestedAt')
                ALTER TABLE [leads] ADD [PendingTransferRequestedAt] datetime2 NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'PendingTransferReason')
                ALTER TABLE [leads] ADD [PendingTransferReason] nvarchar(500) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'bulk_campaigns')
            BEGIN
                CREATE TABLE [bulk_campaigns] (
                    [Id] bigint NOT NULL IDENTITY,
                    [Title] nvarchar(200) NOT NULL,
                    [Category] nvarchar(32) NOT NULL,
                    [TemplateName] nvarchar(120) NULL,
                    [TemplateParams] nvarchar(max) NOT NULL,
                    [CustomMessage] nvarchar(max) NULL,
                    [DriveFileUrl] nvarchar(1000) NULL,
                    [DriveFileName] nvarchar(300) NULL,
                    [Status] nvarchar(32) NOT NULL,
                    [TotalTarget] int NOT NULL,
                    [SentCount] int NOT NULL,
                    [FailedCount] int NOT NULL,
                    [SkippedCount] int NOT NULL,
                    [CreatedBy] nvarchar(200) NOT NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    [CompletedAt] datetime2 NULL,
                    CONSTRAINT [PK_bulk_campaigns] PRIMARY KEY ([Id])
                );
                CREATE INDEX [IX_bulk_campaigns_CreatedAt] ON [bulk_campaigns] ([CreatedAt]);
            END

            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'bulk_campaign_recipients')
            BEGIN
                CREATE TABLE [bulk_campaign_recipients] (
                    [Id] bigint NOT NULL IDENTITY,
                    [CampaignId] bigint NOT NULL,
                    [LeadId] bigint NOT NULL,
                    [Name] nvarchar(200) NOT NULL,
                    [Phone] nvarchar(32) NOT NULL,
                    [Status] nvarchar(32) NOT NULL,
                    [Mode] nvarchar(16) NOT NULL,
                    [Error] nvarchar(500) NULL,
                    [SentAt] datetime2 NULL,
                    CONSTRAINT [PK_bulk_campaign_recipients] PRIMARY KEY ([Id])
                );
                CREATE INDEX [IX_bulk_campaign_recipients_CampaignId_Status] ON [bulk_campaign_recipients] ([CampaignId], [Status]);
            END

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'HeightCm')
                ALTER TABLE [leads] ADD [HeightCm] decimal(6,2) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'WeightKg')
                ALTER TABLE [leads] ADD [WeightKg] decimal(6,2) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'TargetWeightKg')
                ALTER TABLE [leads] ADD [TargetWeightKg] decimal(6,2) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'FitnessGoals')
                ALTER TABLE [leads] ADD [FitnessGoals] nvarchar(max) NOT NULL DEFAULT '[]';

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'DietPreference')
                ALTER TABLE [leads] ADD [DietPreference] nvarchar(64) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'ActivityLevel')
                ALTER TABLE [leads] ADD [ActivityLevel] nvarchar(64) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'ActiveConsultationId')
                ALTER TABLE [leads] ADD [ActiveConsultationId] bigint NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'ConsultationStatus')
                ALTER TABLE [leads] ADD [ConsultationStatus] nvarchar(50) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('leads') AND name = 'ConsultationScheduledAt')
                ALTER TABLE [leads] ADD [ConsultationScheduledAt] datetime2 NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'doctor_consultations')
            BEGIN
                CREATE TABLE [doctor_consultations] (
                    [Id] bigint NOT NULL IDENTITY,
                    [LeadId] bigint NOT NULL,
                    [DoctorEmployeeId] bigint NULL,
                    [BookedByEmployeeId] bigint NOT NULL,
                    [Type] int NOT NULL DEFAULT 1,
                    [Status] int NOT NULL DEFAULT 1,
                    [ScheduledAtUtc] datetime2 NOT NULL,
                    [DurationMinutes] int NOT NULL DEFAULT 20,
                    [MeetingUrl] nvarchar(500) NULL,
                    [DoctorNotes] nvarchar(4000) NULL,
                    [RecommendationsJson] nvarchar(4000) NULL,
                    [CompletedAtUtc] datetime2 NULL,
                    [CompletedByDoctorId] bigint NULL,
                    [SalesFollowUpDone] bit NOT NULL DEFAULT 0,
                    [SalesFollowUpOutcome] nvarchar(50) NULL,
                    [SalesFollowUpReason] nvarchar(500) NULL,
                    [SalesFollowUpAtUtc] datetime2 NULL,
                    [SalesFollowUpByEmployeeId] bigint NULL,
                    [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                    [UpdatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT [PK_doctor_consultations] PRIMARY KEY ([Id])
                );
                CREATE INDEX [IX_doctor_consultations_LeadId] ON [doctor_consultations] ([LeadId]);
                CREATE INDEX [IX_doctor_consultations_DoctorEmployeeId] ON [doctor_consultations] ([DoctorEmployeeId]);
                CREATE INDEX [IX_doctor_consultations_ScheduledAtUtc] ON [doctor_consultations] ([ScheduledAtUtc]);
                CREATE INDEX [IX_doctor_consultations_Status] ON [doctor_consultations] ([Status]);
            END
        ");
        log.LogInformation("[Startup] CRM schema verification completed successfully.");
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "[Startup] CRM schema check warning: {Message}", ex.Message);
    }
}

app.Run();
