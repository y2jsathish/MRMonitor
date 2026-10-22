using System.Threading.Channels;
using AtmNexus.Agent.Cache;
using AtmNexus.Agent.Devices;
using AtmNexus.Agent.Health;

namespace AtmNexus.Agent.Workers;

/// <summary>
/// Receives raw XFS events. The XFS manager invokes the callback on the message-pump thread, so the handler only
/// enqueues (O(1)); mapping to domain events happens here on a worker thread.
/// </summary>
public sealed class XfsEventListener : BackgroundService
{
    private readonly Channel<XfsEvent> _raw = Channel.CreateBounded<XfsEvent>(
        new BoundedChannelOptions(5_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly IXfsServiceManager _xfs;
    private readonly XfsEventProcessor _processor;
    private readonly IDeviceEventBus _bus;
    private readonly ILogger<XfsEventListener> _log;

    public XfsEventListener(IXfsServiceManager xfs, XfsEventProcessor processor, IDeviceEventBus bus, ILogger<XfsEventListener> log)
    {
        _xfs = xfs; _processor = processor; _bus = bus; _log = log;
        _xfs.EventReceived += OnRaw;
    }

    private void OnRaw(XfsEvent e) => _raw.Writer.TryWrite(e);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _raw.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var de = _processor.Process(e);
                    if (de is not null) _bus.TryPublish(de);
                }
                catch (Exception ex) { _log.LogError(ex, "Failed to process XFS {Kind} event {Id} on {Service}", e.Kind, e.EventId, e.LogicalName); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public override void Dispose()
    {
        _xfs.EventReceived -= OnRaw;
        base.Dispose();
    }
}

/// <summary>DevicePollerService: periodic status + cash-unit polling, reconciliation into events, state update.</summary>
public sealed class DevicePollerService(
    IDeviceManager devices, AgentStateStore state, IDeviceEventBus bus, StatusReconciler reconciler,
    IOptions<AgentOptions> options, ILogger<DevicePollerService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await devices.StartAsync(ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(options.Value.DevicePollInterval);
            do
            {
                try { await PollOnceAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Device poll cycle failed"); }
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        var statuses = await devices.PollAllAsync(ct).ConfigureAwait(false);
        foreach (var s in statuses)
        {
            foreach (var e in reconciler.Reconcile(state.GetDevice(s.LogicalName), s)) bus.TryPublish(e);
            state.UpdateDevice(s);
        }

        foreach (var cd in devices.GetAll<ICashDispenser>())
        {
            try
            {
                var units = await cd.GetCashUnitsAsync(ct).ConfigureAwait(false);
                foreach (var e in reconciler.ReconcileCash(cd.LogicalName, state.GetCash(cd.LogicalName), units, DateTimeOffset.UtcNow))
                    bus.TryPublish(e);
                state.UpdateCash(cd.LogicalName, units);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Cash unit poll failed for {LogicalName}", cd.LogicalName);
            }
        }
    }
}

/// <summary>DeviceHealthService: computes the health score on a timer and emits an event when the level changes.</summary>
public sealed class DeviceHealthService(
    AgentStateStore state, HealthScoreEngine engine, AlarmPipeline pipeline, IOptions<AgentOptions> options,
    ILogger<DeviceHealthService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(options.Value.HealthInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await EvaluateAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public async Task<HealthScoreResult> EvaluateAsync(CancellationToken ct)
    {
        var previous = state.LastHealth;
        var result = engine.Calculate(state.Snapshot());
        state.SetHealth(result);

        if (previous is not null && previous.Level != result.Level)
        {
            var sev = result.Level switch
            {
                HealthLevel.Down => AlertSeverity.Critical,
                HealthLevel.Critical => AlertSeverity.Critical,
                HealthLevel.Degraded => AlertSeverity.Warning,
                _ => AlertSeverity.Info
            };
            log.LogInformation("Health level {Previous} -> {Current} (score {Score})", previous.Level, result.Level, result.Score);
            await pipeline.SubmitAsync(new AgentEvent(Guid.NewGuid(), "HEALTH_LEVEL_CHANGED", sev, "health",
                $"Health {previous.Level} -> {result.Level} (score {result.Score})", DateTimeOffset.UtcNow,
                new Dictionary<string, string>
                {
                    ["previous"] = previous.Level.ToString(), ["current"] = result.Level.ToString(),
                    ["score"] = result.Score.ToString(), ["reasons"] = string.Join("; ", result.Reasons.Take(5))
                }), ct).ConfigureAwait(false);
        }
        return result;
    }
}
