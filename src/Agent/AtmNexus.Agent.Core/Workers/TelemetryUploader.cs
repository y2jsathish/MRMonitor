using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AtmNexus.Agent.Cache;
using AtmNexus.Agent.Platform;

namespace AtmNexus.Agent.Workers;

public sealed class BearerTokenHandler(IAgentTokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(ct).ConfigureAwait(false);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Store-and-forward uploader. Everything is written to the SQLite outbox first; this worker drains it to the gateway
/// (outbound HTTPS only, mTLS + OAuth2 client-credentials). Failures back off exponentially with jitter; the ATM never
/// blocks a transaction on telemetry.
/// </summary>
public sealed class TelemetryUploader(
    IHttpClientFactory http, IOutbox outbox, IUploadSignal signal, AgentStateStore state, IOptions<AgentOptions> options,
    ILogger<TelemetryUploader> log) : BackgroundService
{
    public const string ClientName = "gateway";
    private int _failures;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool ok = await FlushAsync(ct).ConfigureAwait(false);
                _failures = ok ? 0 : Math.Min(_failures + 1, 8);
                var wait = ok ? options.Value.TelemetryInterval : Backoff(_failures);
                await signal.WaitAsync(wait, ct).ConfigureAwait(false);   // returns early when a Major/Critical event arrives
            }
        }
        catch (OperationCanceledException) { }
    }

    private static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(900, Math.Pow(2, failures) * 5) * (0.8 + Random.Shared.NextDouble() * 0.4));

    public async Task<bool> FlushAsync(CancellationToken ct)
    {
        var o = options.Value;
        try
        {
            using var client = http.CreateClient(ClientName);

            // 1) side-channel payloads (inventory, EJ batches) go to dedicated endpoints
            foreach (var kind in new[] { "inventory", "ej" })
            {
                foreach (var item in await outbox.PeekAsync(kind, 20, ct).ConfigureAwait(false))
                {
                    using var content = new StringContent(item.Json, System.Text.Encoding.UTF8, "application/json");
                    using var resp = await client.PostAsync($"/api/agent/{kind}", content, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) { log.LogWarning("Upload of {Kind} failed: {Status}", kind, (int)resp.StatusCode); return false; }
                    await outbox.AckAsync([item.Id], ct).ConfigureAwait(false);
                }
            }

            // 2) heartbeat + snapshot + queued events
            var pending = await outbox.PeekAsync("event", o.TelemetryBatchSize, ct).ConfigureAwait(false);
            var events = pending.Select(p => JsonSerializer.Deserialize<AgentEvent>(p.Json, ContractJson.Options)!).ToList();
            var telemetry = Build(o, events);

            using var r = await client.PostAsJsonAsync("/api/agent/telemetry", telemetry, ContractJson.Options, ct).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode) { log.LogWarning("Telemetry upload failed: {Status}", (int)r.StatusCode); return false; }

            await outbox.AckAsync(pending.Select(p => p.Id).ToList(), ct).ConfigureAwait(false);
            await outbox.TrimAsync(o.OutboxMaxRows, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) return false;
            log.LogWarning(ex, "Gateway unreachable; telemetry stays queued ({Failures} consecutive failures)", _failures + 1);
            return false;
        }
    }

    internal AtmTelemetry Build(AgentOptions o, IReadOnlyList<AgentEvent> events)
    {
        var health = state.LastHealth;
        var devices = state.Devices().Select(d => new DeviceSnapshot(d.LogicalName, d.Class, d.State, null, null, null, d.Attributes, d.At)).ToList();
        var cash = state.AllCashUnits().Select(u => new CashUnitSnapshot(u.Number, u.UnitId, u.Type.ToString(), u.Currency, u.Denomination,
            u.Count, u.InitialCount, u.RejectCount, u.Minimum, u.Maximum, u.Status.ToString())).ToList();
        var metrics = new Dictionary<string, double>();
        if (health is not null) foreach (var c in health.Components) metrics[$"health.{c.Key}"] = c.Value;

        return new AtmTelemetry(o.AtmCode, o.AgentVersion, DateTimeOffset.UtcNow, health?.Score ?? 100,
            health?.Level ?? HealthLevel.Healthy, devices, cash, events, metrics);
    }
}
