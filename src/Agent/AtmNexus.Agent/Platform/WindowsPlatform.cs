using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Net.Sockets;
using System.ServiceProcess;
using AtmNexus.Agent.Cache;
using AtmNexus.Agent.Platform;
using AtmNexus.Agent.ProBase;
using Microsoft.Win32;

namespace AtmNexus.Agent.Platform.Windows;

public sealed class ScmServiceController : IWindowsServiceController
{
    public Task<WinServiceState> GetStateAsync(string serviceName, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => WinServiceState.Running,
                ServiceControllerStatus.Stopped => WinServiceState.Stopped,
                ServiceControllerStatus.StartPending => WinServiceState.StartPending,
                ServiceControllerStatus.StopPending => WinServiceState.StopPending,
                ServiceControllerStatus.Paused or ServiceControllerStatus.PausePending or ServiceControllerStatus.ContinuePending => WinServiceState.Paused,
                _ => WinServiceState.Unknown
            };
        }
        catch (InvalidOperationException) { return WinServiceState.NotFound; }
    }, ct);

    public Task RestartAsync(string serviceName, TimeSpan timeout, CancellationToken ct) => Task.Run(() =>
    {
        using var sc = new ServiceController(serviceName);
        if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }, ct);
}

public sealed class SafeProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        // Bare names resolve to System32 only: no PATH lookup, no shell, no string-built command line.
        var path = Path.IsPathRooted(fileName) ? fileName : Path.Combine(Environment.SystemDirectory, fileName);
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {path}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = p.StandardError.ReadToEndAsync(cts.Token);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
        return new ProcessResult(p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    public bool IsRunning(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        var procs = Process.GetProcessesByName(name);
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }
}

/// <summary>Reads Error/Critical events from System, Application and vendor channels using persisted EventRecordID bookmarks.</summary>
public sealed class WindowsEventLogSource(ICheckpointStore checkpoints, ILogger<WindowsEventLogSource> log) : IWindowsEventSource
{
    private static readonly string[] Logs = ["System", "Application"];

    public async Task<IReadOnlyList<WindowsEventRecord>> ReadNewAsync(CancellationToken ct)
    {
        var result = new List<WindowsEventRecord>();
        foreach (var logName in Logs)
        {
            string key = "winevt:" + logName;
            long last = (await checkpoints.GetAsync(key, ct).ConfigureAwait(false))?.Offset ?? 0;
            string xpath = last == 0
                ? "*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= 900000]]]"   // first run: last 15 min only
                : $"*[System[(Level=1 or Level=2) and EventRecordID > {last}]]";
            long max = last;
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, xpath));
                for (EventRecord? rec = reader.ReadEvent(); rec is not null; rec = reader.ReadEvent())
                {
                    using (rec)
                    {
                        string msg;
                        try { msg = rec.FormatDescription() ?? ""; } catch (EventLogException) { msg = $"Event {rec.Id} (description unavailable)"; }
                        result.Add(new WindowsEventRecord(logName, rec.ProviderName ?? "unknown", rec.Id,
                            rec.Level == 1 ? AlertSeverity.Critical : AlertSeverity.Major,
                            rec.TimeCreated is { } t ? new DateTimeOffset(t.ToUniversalTime()) : DateTimeOffset.UtcNow, msg));
                        max = Math.Max(max, rec.RecordId ?? 0);
                    }
                }
            }
            catch (EventLogException ex) { log.LogWarning(ex, "Cannot read event log {Log}", logName); }
            if (max > last) await checkpoints.SetAsync(key, max, null, ct).ConfigureAwait(false);
        }
        return result;
    }
}

public sealed class RegistrySoftwareInventory : ISoftwareInventory
{
    private static readonly string[] Roots =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    public Task<IReadOnlyList<SoftwareItem>> ListAsync(CancellationToken ct) => Task.Run<IReadOnlyList<SoftwareItem>>(() =>
    {
        var items = new List<SoftwareItem>();
        foreach (var root in Roots)
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(sub);
                var name = k?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name) || k?.GetValue("SystemComponent") is 1) continue;
                DateOnly? date = DateTime.TryParseExact(k!.GetValue("InstallDate") as string, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d) ? DateOnly.FromDateTime(d) : null;
                items.Add(new SoftwareItem(name, k.GetValue("DisplayVersion") as string ?? "", k.GetValue("Publisher") as string, date));
            }
        }
        return items.DistinctBy(i => (i.Name, i.Version)).OrderBy(i => i.Name).ToList();
    }, ct);
}

public sealed class WmiPatchInventory : IPatchInventory
{
    public Task<IReadOnlyList<PatchItem>> ListAsync(CancellationToken ct) => Task.Run<IReadOnlyList<PatchItem>>(() =>
    {
        var list = new List<PatchItem>();
        using var searcher = new ManagementObjectSearcher("SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering");
        foreach (ManagementBaseObject o in searcher.Get())
        {
            DateOnly? on = DateTime.TryParse(o["InstalledOn"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? DateOnly.FromDateTime(d) : null;
            list.Add(new PatchItem(o["HotFixID"]?.ToString() ?? "", o["Description"]?.ToString(), on));
        }
        return list;
    }, ct);
}

/// <summary>ICMP is usually blocked in bank networks: measure TCP connect time/loss to the gateway port instead.</summary>
public sealed class TcpNetworkProbe(IOptions<AgentOptions> options) : INetworkProbe
{
    public async Task<NetworkProbeResult> ProbeAsync(CancellationToken ct)
    {
        var uri = new Uri(options.Value.GatewayBaseUrl);
        const int attempts = 4;
        int ok = 0;
        double total = 0;
        for (int i = 0; i < attempts; i++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var tcp = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync(uri.Host, uri.Port, cts.Token).ConfigureAwait(false);
                ok++; total += sw.Elapsed.TotalMilliseconds;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested) { }
        }
        return new NetworkProbeResult(ok > 0, ok == 0 ? 0 : total / ok, 100.0 * (attempts - ok) / attempts);
    }
}

/// <summary>Idle = no open ProBase transaction and 60 s of quiet. Swap for a vendor probe (e.g. APTRA state) on non-ProBase ATMs.</summary>
public sealed class ProBaseActivityProbe(ProBaseHealthMonitor monitor) : IAtmActivityProbe
{
    public Task<bool> IsIdleAsync(CancellationToken ct) => Task.FromResult(monitor.IsIdle(TimeSpan.FromSeconds(60)));
}
