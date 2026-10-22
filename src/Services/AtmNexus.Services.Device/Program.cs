using System.Threading.RateLimiting;
using AtmNexus.Services.Device.Endpoints;
using AtmNexus.Services.Device.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

DefaultTypeMap.MatchNamesWithUnderscores = true;        // snake_case columns -> PascalCase properties

// ---------------- logging / telemetry ----------------
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();                       // structured logs -> Elastic via Filebeat/Fluent Bit
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("atmnexus-device-service", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation()
        .AddMeter("AtmNexus.Device", "Npgsql").AddOtlpExporter())
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSource("AtmNexus.Device", "Npgsql").AddOtlpExporter());

// ---------------- persistence ----------------
string cs = builder.Configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
builder.Services.AddSingleton(_ =>
{
    var b = new NpgsqlDataSourceBuilder(cs);
    return b.Build();
});
builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: ["live"])
    .AddNpgSql(cs, name: "postgres", tags: ["ready"]);

// ---------------- security: OIDC/JWT + permission-based RBAC ----------------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.Authority = builder.Configuration["Auth:Authority"];
    o.Audience = builder.Configuration["Auth:Audience"] ?? "atmnexus-api";
    o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    o.MapInboundClaims = false;                          // keep "sub", "scope", "permissions" as issued
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, RequireSignedTokens = true,
        ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = "preferred_username", RoleClaimType = "role",
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256]
    };
});
builder.Services.AddAuthorization(o =>
{
    foreach (var perm in Permissions.All)
        o.AddPolicy(perm, p => p.RequireAuthenticatedUser().RequireClaim("permissions", perm));
    o.AddPolicy(Permissions.AgentPolicy, p => p.RequireAuthenticatedUser().RequireClaim("scope", "atm.agent").RequireClaim("client_id"));
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("agent", ctx => RateLimitPartition.GetTokenBucketLimiter(ctx.User.FindFirst("client_id")?.Value ?? "anonymous",
        _ => new TokenBucketRateLimiterOptions { TokenLimit = 30, TokensPerPeriod = 10, ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// ---------------- application services ----------------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var c in ContractJson.Options.Converters) o.SerializerOptions.Converters.Add(c);
    o.SerializerOptions.DefaultIgnoreCondition = ContractJson.Options.DefaultIgnoreCondition;
});

builder.Services.Configure<SigningOptions>(builder.Configuration.GetSection(SigningOptions.Section));
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.Section));
builder.Services.Configure<OfflineDetectorOptions>(builder.Configuration.GetSection(OfflineDetectorOptions.Section));

builder.Services.AddSingleton<ICommandSigner, PemCommandSigner>();
builder.Services.AddSingleton<CommandNotifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CommandNotifier>());
builder.Services.AddSingleton<AtmRepository>();
builder.Services.AddSingleton<AuditWriter>();
builder.Services.AddSingleton<CommandService>();
builder.Services.AddSingleton<IngestTelemetryHandler>();
builder.Services.AddSingleton<EventSideChannel>();
builder.Services.AddSingleton<ILogStore, FileLogStore>();
builder.Services.AddHostedService<OutboxRelayService>();
builder.Services.AddHostedService<OfflineDetectorService>();

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
app.UseExceptionHandler();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new() { Predicate = r => r.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("ready") });
app.MapAtmEndpoints();
app.MapAgentEndpoints();

app.Run();

public partial class Program;
