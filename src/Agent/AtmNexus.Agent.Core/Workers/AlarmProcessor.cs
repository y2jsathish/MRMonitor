using System.Text.Json;
using System.Text.RegularExpressions;
using AtmNexus.Agent.Cache;

namespace AtmNexus.Agent.Workers;

/// <summary>De-duplicates, flap-detects and persists agent events to the outbox. Shared by every producer (devices, services, Windows events, ProBase).</summary>
public sealed class AlarmPipeline(IOutbox outbox, IUploadSignal signal, IOptions<AlarmOptions> options, TimeProvider clock, ILogger<AlarmPipeline> log)
{
    private readonly AlarmOptions _opt = options.Value;
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _recent = new();
    private readonly HashSet<string> _flapping = new();
    private readonly object _gate = new();

    public async Task<bool> SubmitAsync(AgentEvent e, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        string key = $"{e.Source}|{e.Code}|{e.Data?.GetValueOrDefault("unit")}|{e.Data?.GetValueOrDefault("service")}";
        bool flapEvent = false;

        lock (_gate)
        {
            if (_lastSeen.TryGetValue(key, out var last) && now - last < _opt.DedupWindow)
            {
                log.LogDebug("Suppressed duplicate {Code} from {Source}", e.Code, e.Source);
                return false;
            }
            _lastSeen[key] = now;

            var q = _recent.TryGetValue(key, out var existing) ? existing : _recent[key] = new Queue<DateTimeOffset>();
            q.Enqueue(now);
            while (q.Count > 0 && now - q.Peek() > _opt.FlapWindow) q.Dequeue();
            if (q.Count >= _opt.FlapThreshold && _flapping.Add(key)) flapEvent = true;
            else if (q.Count < _opt.FlapThreshold) _flapping.Remove(key);

            if (_lastSeen.Count > 5_000)
            {
                foreach (var k in _lastSeen.Where(kv => now - kv.Value > _opt.DedupWindow).Select(kv => kv.Key).ToList())
                {
                    _lastSeen.Remove(k);
                    _recent.Remove(k);
                    _flapping.Remove(k);
                }
            }
        }

        await outbox.EnqueueAsync("event", JsonSerializer.Serialize(e, ContractJson.Options), ct).ConfigureAwait(false);
        if (flapEvent)
        {
            var flap = new AgentEvent(Guid.NewGuid(), "FLAPPING", AlertSeverity.Major, e.Source,
                $"{e.Code} raised {_opt.FlapThreshold}+ times in {_opt.FlapWindow.TotalMinutes:F0} min", now,
                new Dictionary<string, string> { ["flappingCode"] = e.Code });
            await outbox.EnqueueAsync("event", JsonSerializer.Serialize(flap, ContractJson.Options), ct).ConfigureAwait(false);
        }
        if (e.Severity >= AlertSeverity.Major) signal.Trigger();   // don't wait for the next telemetry tick
        return true;
    }
}

/// <summary>Consumes the device event bus: updates ATM state, then hands events to the pipeline.</summary>
public sealed partial class AlarmProcessor(
    IDeviceEventBus bus, AgentStateStore state, AlarmPipeline pipeline, ILogger<AlarmProcessor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in bus.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try { await ProcessAsync(e, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Alarm processing failed for {Type}", e.Type); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task ProcessAsync(DeviceEvent e, CancellationToken ct)
    {
        state.RecordEvent(e);
        if (e.Type == DeviceEventType.Unknown && e.Severity == AlertSeverity.Info)
        {
            log.LogDebug("Unmapped event {Message}", e.Message);
            return;
        }
        var data = new Dictionary<string, string>(e.Data) { ["device"] = e.LogicalName, ["deviceClass"] = e.Class.ToString() };
        var ae = new AgentEvent(e.Id, ToCode(e.Type), e.Severity, e.LogicalName, e.Message, e.At, data);
        await pipeline.SubmitAsync(ae, ct).ConfigureAwait(false);
    }

    /// <summary>CashLow -> CASH_LOW</summary>
    public static string ToCode(DeviceEventType t) => Camel().Replace(t.ToString(), "$1_$2").ToUpperInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex Camel();
}
