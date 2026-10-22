using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using AtmNexus.Services.Device.Application;
using AtmNexus.Services.Device.Infrastructure;

namespace AtmNexus.Services.Device.Endpoints;

public static class Permissions
{
    public const string AtmRead = "atm.read";
    public const string CommandExecute = "atm.command.execute";
    public const string CommandApprove = "atm.command.approve";
    public const string AgentPolicy = "agent";
    public static readonly string[] All = [AtmRead, CommandExecute, CommandApprove, "alert.read", "alert.ack", "incident.read", "incident.manage",
        "deployment.read", "deployment.manage", "deployment.approve", "user.read", "user.manage", "audit.read", "report.read", "atm.manage"];
}

public sealed record ListParams(string? Status, string? Connectivity, string? Branch, string? Vendor, string? Search, int Page = 1, int PageSize = 50);

public static partial class AtmEndpoints
{
    [GeneratedRegex(@"^[A-Za-z0-9_. -]{1,64}$")]
    private static partial Regex SafeName();

    public static IEndpointRouteBuilder MapAtmEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/atms", async ([AsParameters] ListParams p, AtmRepository repo, CancellationToken ct) =>
                TypedResults.Ok(await repo.ListAsync(new ListAtmsQuery(p.Status, p.Connectivity, p.Branch, p.Vendor, p.Search, p.Page, p.PageSize), ct)))
            .RequireAuthorization(Permissions.AtmRead).WithName("ListAtms");

        api.MapGet("/atms/{id:guid}", async Task<Results<Ok<AtmDetail>, NotFound>> (Guid id, AtmRepository repo, CancellationToken ct) =>
                await repo.GetAsync(id, ct) is { } atm ? TypedResults.Ok(atm) : TypedResults.NotFound())
            .RequireAuthorization(Permissions.AtmRead).WithName("GetAtm");

        api.MapGet("/atms/{id:guid}/status", async Task<Results<Ok<AtmStatusDto>, NotFound>> (Guid id, AtmRepository repo, CancellationToken ct) =>
            {
                var s = await repo.GetStatusAsync(id, ct);
                if (s is null) return TypedResults.NotFound();
                var comps = await repo.GetComponentsAsync(id, ct);
                var cash = await repo.GetCashUnitsAsync(id, ct);
                var alerts = await repo.GetOpenAlertsAsync(id, ct);
                return TypedResults.Ok(new AtmStatusDto(s.AtmId, s.AtmCode, s.HealthScore, s.HealthLevel, s.Connectivity, s.InService, s.LastSeenAt,
                    JsonSerializer.Deserialize<Dictionary<string, int>>(s.ComponentsJson) ?? new(),
                    JsonSerializer.Deserialize<List<string>>(s.ReasonsJson) ?? new(),
                    comps.Select(c => new DeviceStatusDto(c.LogicalName, c.DeviceClass, c.State, AtmRepository.ParseAttributes(c.AttributesJson), c.LastStateChangeAt)).ToList(),
                    cash, alerts.Count));
            })
            .RequireAuthorization(Permissions.AtmRead).WithName("GetAtmStatus");

        // Digital twin: device state model + health model + fault model in one document
        api.MapGet("/atms/{id:guid}/twin", async Task<Results<Ok<TwinDocument>, NotFound>> (Guid id, AtmRepository repo, TimeProvider clock, CancellationToken ct) =>
            {
                var s = await repo.GetStatusAsync(id, ct);
                if (s is null) return TypedResults.NotFound();
                var twin = DigitalTwinBuilder.Build(s, await repo.GetComponentsAsync(id, ct), await repo.GetCashUnitsAsync(id, ct),
                    await repo.GetOpenAlertsAsync(id, ct), clock.GetUtcNow().UtcDateTime);
                return TypedResults.Ok(twin);
            })
            .RequireAuthorization(Permissions.AtmRead).WithName("GetAtmTwin");

        api.MapGet("/dashboard", async (AtmRepository repo, CancellationToken ct) => TypedResults.Ok(await repo.GetDashboardAsync(ct)))
            .RequireAuthorization(Permissions.AtmRead).WithName("GetDashboard");

        api.MapPost("/atms/{id:guid}/restart", async Task<Results<Accepted<QueuedCommand>, ValidationProblem>> (
                Guid id, RestartRequest body, CommandService commands, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var errors = new Dictionary<string, string[]>();
                var target = body.Target?.ToLowerInvariant();
                RemoteCommandType? type = target switch
                {
                    "application" => RemoteCommandType.RestartApplication,
                    "xfs" => RemoteCommandType.RestartXfs,
                    "device" => RemoteCommandType.RestartDevice,
                    "service" => RemoteCommandType.RestartWindowsService,
                    "atm" => RemoteCommandType.RebootAtm,
                    _ => null
                };
                if (type is null) errors["target"] = ["target must be one of: application, xfs, device, service, atm"];
                if (type is RemoteCommandType.RestartDevice or RemoteCommandType.RestartWindowsService && (body.Name is null || !SafeName().IsMatch(body.Name)))
                    errors["name"] = ["name is required (1-64 chars: letters, digits, space, . _ -) for device/service restarts"];
                if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > 500) errors["reason"] = ["reason is required (max 500 chars)"];
                if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

                var p = new Dictionary<string, string>();
                if (type == RemoteCommandType.RestartDevice) p["device"] = body.Name!;
                if (type == RemoteCommandType.RestartWindowsService) p["service"] = body.Name!;

                var issuer = user.FindFirst("sub")?.Value ?? throw new ForbiddenException("Missing subject claim.");
                var queued = await commands.QueueAsync(id, type!.Value, p, issuer, body.Reason, ct);
                return TypedResults.Accepted($"/api/commands/{queued.CommandId}", queued);
            })
            .RequireAuthorization(Permissions.CommandExecute).WithName("RestartAtm");

        api.MapPost("/commands/{id:guid}/approve", async (Guid id, CommandService commands, ClaimsPrincipal user, CancellationToken ct) =>
                TypedResults.Accepted($"/api/commands/{id}", await commands.ApproveAsync(id, user.FindFirst("sub")?.Value ?? throw new ForbiddenException("Missing subject claim."), ct)))
            .RequireAuthorization(Permissions.CommandApprove).WithName("ApproveCommand");

        return app;
    }

    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup("/api/agent").RequireAuthorization(Permissions.AgentPolicy);

        agent.MapPost("/telemetry", async (AtmTelemetry body, IngestTelemetryHandler handler, ClaimsPrincipal user, CancellationToken ct) =>
                TypedResults.Ok(await handler.HandleAsync(body, AtmCode(user), ct)))
            .RequireRateLimiting("agent").WithName("IngestTelemetry");

        agent.MapGet("/commands/next", async Task<Results<Ok<SignedRemoteCommand>, NoContent>> (
                [FromQuery] int? waitSeconds, CommandService commands, ClaimsPrincipal user, CancellationToken ct) =>
            await commands.DequeueAsync(AtmCode(user), TimeSpan.FromSeconds(Math.Clamp(waitSeconds ?? 0, 0, 30)), ct) is { } cmd
                ? TypedResults.Ok(cmd) : TypedResults.NoContent())
            .WithName("NextCommand");

        agent.MapPost("/commands/{id:guid}/result", async (Guid id, RemoteCommandResult body, CommandService commands, ClaimsPrincipal user, CancellationToken ct) =>
            {
                if (body.CommandId != id) throw new ValidationFailedException("commandId in body does not match the URL.");
                await commands.CompleteAsync(AtmCode(user), id, body, ct);
                return TypedResults.NoContent();
            })
            .WithName("CompleteCommand");

        // Inventory and EJ batches are accepted fast and fanned out through Kafka (consumed by Reporting / EJ indexer).
        agent.MapPost("/inventory", (JsonElement body, EventSideChannel ch, ClaimsPrincipal user, CancellationToken ct) => ch.AcceptAsync("atm.inventory.reported", AtmCode(user), body, ct))
            .WithMetadata(new RequestSizeLimitAttribute(5_000_000)).WithName("IngestInventory");
        agent.MapPost("/ej", (JsonElement body, EventSideChannel ch, ClaimsPrincipal user, CancellationToken ct) => ch.AcceptAsync(Topics.EjBatch, AtmCode(user), body, ct))
            .WithMetadata(new RequestSizeLimitAttribute(5_000_000)).WithName("IngestEj");

        agent.MapPut("/logs/{label}", async (string label, HttpRequest req, ILogStore store, ClaimsPrincipal user, CancellationToken ct) =>
            {
                if (!Regex.IsMatch(label, "^[A-Za-z0-9-]{8,64}$")) throw new ValidationFailedException("invalid label");
                var uri = await store.SaveAsync(AtmCode(user), label, req.Body, ct);
                return TypedResults.Accepted(uri);
            })
            .WithMetadata(new RequestSizeLimitAttribute(250_000_000)).WithName("UploadLogs");

        return app;
    }

    private static string AtmCode(ClaimsPrincipal u) =>
        u.FindFirst("client_id")?.Value ?? u.FindFirst("atm_code")?.Value ?? throw new ForbiddenException("Token carries no ATM identity.");
}

/// <summary>Accepts agent payloads and publishes them through the transactional outbox.</summary>
public sealed class EventSideChannel(Npgsql.NpgsqlDataSource db, TimeProvider clock)
{
    public async Task<IResult> AcceptAsync(string topic, string atmCode, JsonElement body, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var atmId = await Dapper.SqlMapper.ExecuteScalarAsync<Guid?>(conn, Sql.Cmd("SELECT atm_id FROM atm WHERE atm_code=@atmCode", new { atmCode }, null, ct))
                    ?? throw new NotFoundException($"ATM '{atmCode}' is not registered.");
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Sql.EnqueueEventAsync(conn, tx, topic, atmId, topic, body, clock, ct);
        await tx.CommitAsync(ct);
        return TypedResults.Accepted((string?)null);
    }
}

public interface ILogStore { Task<string> SaveAsync(string atmCode, string label, Stream content, CancellationToken ct); }

/// <summary>Dev/on-prem implementation. Production: Azure Blob Storage with immutable, customer-managed-key encrypted containers.</summary>
public sealed class FileLogStore(IConfiguration cfg) : ILogStore
{
    public async Task<string> SaveAsync(string atmCode, string label, Stream content, CancellationToken ct)
    {
        string root = cfg["LogStore:Directory"] ?? Path.Combine(AppContext.BaseDirectory, "logstore");
        string safeAtm = Regex.Replace(atmCode, "[^A-Za-z0-9_-]", "_");
        Directory.CreateDirectory(Path.Combine(root, safeAtm));
        string path = Path.Combine(root, safeAtm, label + ".zip");
        await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(fs, ct);
        return $"/api/logs/{safeAtm}/{label}";
    }
}

/// <summary>RFC 7807 problem+json for every failure; never leaks stack traces or SQL.</summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> log, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var (status, title) = ex switch
        {
            NotFoundException => (404, "Not found"),
            ForbiddenException => (403, "Forbidden"),
            ValidationFailedException => (400, "Validation failed"),
            ConflictException => (409, "Conflict"),
            _ => (500, "Unexpected error")
        };
        if (status == 500) log.LogError(ex, "Unhandled exception on {Path}", ctx.Request.Path);
        ctx.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = ctx,
            ProblemDetails = { Status = status, Title = title, Detail = status == 500 ? "An internal error occurred." : ex.Message }
        });
    }
}
