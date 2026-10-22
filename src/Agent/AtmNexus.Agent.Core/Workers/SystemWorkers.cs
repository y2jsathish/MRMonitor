using System.Text.Json;
using AtmNexus.Agent.Cache;
using AtmNexus.Agent.Health;
using AtmNexus.Agent.Platform;

namespace AtmNexus.Agent.Workers;

/// <summary>Service + process + network monitoring with guarded auto-restart; feeds ApplicationStatus/NetworkStatus into health scoring.</summary>
public sealed class ServiceMonitorWorker(
    IWindowsServiceController scm, IProcessRunner processes, INetworkProbe network, IAtmActivityProbe activity,
    AgentStateStore state, AlarmPipeline pipeline, IOptions<AgentOptions> options, TimeProvider clock,
    ILogger<ServiceMonitorWorker> log) : BackgroundService
{
    private readonly Dictionary<string, Queue<DateTimeOffset>> _restarts = new();
    private readonly Dictionary<string, WinServiceState> _last = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(options.Value.ServicePollInterval);
            do { await CycleAsync(ct).ConfigureAwait(false); } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    public async Task CycleAsync(CancellationToken ct)
    {
        var o = options.Value;
        bool appRunning = true, xfsRunning = true;

        foreach (var svc in o.Services)
        {
            var st = await scm.GetStateAsync(svc.Name, ct).ConfigureAwait(false);
            bool changed = !_last.TryGetValue(svc.Name, out var prev) || prev != st;
            _last[svc.Name] = st;

            if (st != WinServiceState.Running)
            {
                if (svc.Role == "Xfs") xfsRunning = false;
                else if (svc.Critical || svc.Role == "AtmApp") appRunning = false;
                if (changed)
                    await pipeline.SubmitAsync(new AgentEvent(Guid.NewGuid(), "SERVICE_NOT_RUNNING",
                        svc.Critical ? AlertSeverity.Critical : AlertSeverity.Major, "services",
                        $"Windows service '{svc.Name}' is {st}", clock.GetUtcNow(),
                        new Dictionary<string, string> { ["service"] = svc.Name, ["state"] = st.ToString() }), ct).ConfigureAwait(false);

                if (svc.AutoRestart && st is WinServiceState.Stopped && await CanRestartAsync(svc, ct).ConfigureAwait(false))
                {
                    log.LogWarning("Auto-restarting service {Service}", svc.Name);
                    try { await scm.RestartAsync(svc.Name, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Auto-restart of {Service} failed", svc.Name); }
                }
            }
        }

        foreach (var p in o.Processes)
            if (!processes.IsRunning(p)) { appRunning = false; log.LogWarning("Process {Process} not running", p); }

        var net = await network.ProbeAsync(ct).ConfigureAwait(false);
        state.UpdateApplication(a => a with { AtmAppRunning = appRunning, XfsManagerRunning = xfsRunning });
        state.SetNetwork(new NetworkStatus(net.Reachable, net.LatencyMs, net.PacketLossPct));
    }

    private async Task<bool> CanRestartAsync(MonitoredService svc, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var q = _restarts.TryGetValue(svc.Name, out var e) ? e : _restarts[svc.Name] = new Queue<DateTimeOffset>();
        while (q.Count > 0 && now - q.Peek() > TimeSpan.FromHours(1)) q.Dequeue();
        if (q.Count >= svc.MaxAutoRestartsPerHour) return false;        // restart loop => leave it for a human
        if (!await activity.IsIdleAsync(ct).ConfigureAwait(false)) return false;   // never restart under a customer
        q.Enqueue(now);
        return true;
    }
}

/// <summary>Collects Windows Error/Critical events (System, Application, vendor channels) via a bookmarked reader.</summary>
public sealed class WindowsEventWorker(IWindowsEventSource source, AlarmPipeline pipeline, ILogger<WindowsEventWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do
            {
                try
                {
                    foreach (var r in (await source.ReadNewAsync(ct).ConfigureAwait(false)).Take(50))
                        await pipeline.SubmitAsync(new AgentEvent(Guid.NewGuid(), $"WINEVT_{r.Provider}_{r.EventId}".Replace(' ', '_').ToUpperInvariant(),
                            r.Severity, "windows-events", Truncate(r.Message, 500), r.At,
                            new Dictionary<string, string> { ["log"] = r.Log, ["provider"] = r.Provider, ["eventId"] = r.EventId.ToString() }), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Windows event collection failed"); }
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>Software + patch inventory once per interval; also derives the patch-compliance flag used by health scoring.</summary>
public sealed class InventoryWorker(
    ISoftwareInventory software, IPatchInventory patches, IOutbox outbox, IUploadSignal signal, AgentStateStore state,
    IOptions<AgentOptions> options, ILogger<InventoryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(options.Value.InventoryInterval);
            do
            {
                try { await CollectAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Inventory collection failed"); }
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    public async Task CollectAsync(CancellationToken ct)
    {
        var sw = await software.ListAsync(ct).ConfigureAwait(false);
        var hf = await patches.ListAsync(ct).ConfigureAwait(false);

        var newest = hf.Where(p => p.InstalledOn.HasValue).Select(p => p.InstalledOn!.Value).DefaultIfEmpty().Max();
        bool compliant = newest != default && newest >= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));
        state.SetPatchCompliance(compliant);

        var doc = new { atmCode = options.Value.AtmCode, collectedAt = DateTimeOffset.UtcNow, software = sw, patches = hf, patchCompliant = compliant };
        await outbox.EnqueueAsync("inventory", JsonSerializer.Serialize(doc, ContractJson.Options), ct).ConfigureAwait(false);
        signal.Trigger();
        log.LogInformation("Inventory collected: {Software} packages, {Patches} hotfixes, compliant={Compliant}", sw.Count, hf.Count, compliant);
    }
}
