using System.Globalization;
using System.Text.Json;
using AtmNexus.Agent.Ej;
using AtmNexus.Agent.Platform;

namespace AtmNexus.Agent.Commands;

internal static class CommandParams
{
    public static string Required(this RemoteCommand c, string name) =>
        c.Parameters.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v : throw new CommandRejectedException($"missing parameter '{name}'");

    public static async Task EnsureIdleAsync(IAtmActivityProbe activity, RemoteCommand c, CancellationToken ct)
    {
        bool force = c.Parameters.TryGetValue("force", out var f) && f == "true" && !string.IsNullOrWhiteSpace(c.ApprovedBy);
        if (!force && !await activity.IsIdleAsync(ct).ConfigureAwait(false))
            throw new CommandRejectedException("ATM is busy with a customer transaction; retry later or issue an approved force command");
    }
}

public sealed class RestartWindowsServiceHandler(
    IWindowsServiceController scm, IAtmActivityProbe activity, IOptions<CommandPolicy> policy) : IRemoteCommandHandler
{
    public RemoteCommandType Type => RemoteCommandType.RestartWindowsService;

    public async Task<string> HandleAsync(RemoteCommand c, CancellationToken ct)
    {
        string name = c.Required("service");
        if (!policy.Value.AllowedServices.Contains(name)) throw new CommandRejectedException($"service '{name}' is not on the allow-list");
        await CommandParams.EnsureIdleAsync(activity, c, ct).ConfigureAwait(false);
        await scm.RestartAsync(name, TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
        return $"service '{name}' restarted; state={await scm.GetStateAsync(name, ct).ConfigureAwait(false)}";
    }
}

public sealed class RestartApplicationHandler(
    IWindowsServiceController scm, IAtmActivityProbe activity, IOptions<CommandPolicy> policy) : IRemoteCommandHandler
{
    public RemoteCommandType Type => RemoteCommandType.RestartApplication;

    public async Task<string> HandleAsync(RemoteCommand c, CancellationToken ct)
    {
        var app = policy.Value.AtmApplicationService;
        if (string.IsNullOrEmpty(app)) throw new CommandRejectedException("ATM application service is not configured");
        await CommandParams.EnsureIdleAsync(activity, c, ct).ConfigureAwait(false);
        await scm.RestartAsync(app, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        return $"application service '{app}' restarted";
    }
}

/// <summary>Restarts the XFS manager service and then the ATM application that depends on it.</summary>
public sealed class RestartXfsHandler(
    IWindowsServiceController scm, IAtmActivityProbe activity, IOptions<CommandPolicy> policy) : IRemoteCommandHandler
{
    public RemoteCommandType Type => RemoteCommandType.RestartXfs;

    public async Task<string> HandleAsync(RemoteCommand c, CancellationToken ct)
    {
        var p = policy.Value;
        if (string.IsNullOrEmpty(p.XfsServiceName)) throw new CommandRejectedException("XFS service is not configured");
        await CommandParams.EnsureIdleAsync(activity, c, ct).ConfigureAwait(false);
        await scm.RestartAsync(p.XfsServiceName, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(p.AtmApplicationService))
            await scm.RestartAsync(p.AtmApplicationService, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        return "XFS manager and ATM application restarted";
    }
}

public sealed class RestartDeviceHandler(IDeviceManager devices, IAtmActivityProbe activity) : IRemoteCommandHandler
{
    public RemoteCommandType Type => RemoteCommandType.RestartDevice;

    public async Task<string> HandleAsync(RemoteCommand c, CancellationToken ct)
    {
        string name = c.Required("device");
        var dev = devices.Get<IATMDevice>(name) ?? throw new CommandRejectedException($"unknown device '{name}'");
        await CommandParams.EnsureIdleAsync(activity, c, ct).ConfigureAwait(false);
        await dev.ResetAsync(ct).ConfigureAwait(false);   // XFS WFS_CMD_*_RESET / XFS4IoT Common.Reset
        var st = await dev.GetStatusAsync(ct).ConfigureAwait(false);
        return $"device '{name}' reset; state={st.State}";
    }
}

public sealed class CollectLogsHandler(ILogBundleUploader uploader, IOptions<CommandPolicy> policy) : IRemoteCommandHandler
{
    public RemoteCommandType Type => RemoteCommandType.CollectLogs;

    public async Task<string> HandleAsync(RemoteCommand c, CancellationToken ct)
    {
        int hours = c.Parameters.TryGetValue("hours", out var h) && int.TryParse(h, out var n) ? Math.Clamp(n, 1, 168) : 24;
        if (policy.Value.LogDirectories.Count == 0) throw new CommandRejectedException("no log directories are configured");
        var url = await uploader.BundleAndUploadAsync(policy.Value.LogDirectories, TimeSpan.FromHours(hours), c.CommandId.ToString("N"), ct).ConfigureAwait(false);
        return url;
    }
}

public sealed class CollectEjHandler(IEjCollectionTrigger ej) : IRemoteCommandHandler
{
    public RemoteCommandType Type => RemoteCommandType.CollectEj;

    public async Task<string> HandleAsync(RemoteCommand c, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(c.Required("from"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
            !DateOnly.TryParseExact(c.Required("to"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
            throw new CommandRejectedException("from/to must be yyyy-MM-dd");
        if (to < from || to.DayNumber - from.DayNumber > 31) throw new CommandRejectedException("range must be 0-31 days");
        int count = await ej.CollectAsync(from, to, ct).ConfigureAwait(false);
        return $"{count} EJ transactions queued for upload";
    }
}

public sealed class RunDiagnosticsHandler(IDeviceManager devices, INetworkProbe network, AgentStateStore state) : IRemoteCommandHandler
{
    public RemoteCommandType Type => RemoteCommandType.RunDiagnostics;

    public async Task<string> HandleAsync(RemoteCommand c, CancellationToken ct)
    {
        var statuses = await devices.PollAllAsync(ct).ConfigureAwait(false);
        var net = await network.ProbeAsync(ct).ConfigureAwait(false);
        var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
        var report = new
        {
            at = DateTimeOffset.UtcNow,
            health = state.LastHealth,
            devices = statuses.Select(s => new { s.LogicalName, s.Class, s.State, s.Attributes }),
            network = net,
            disk = new { freeGb = drive.AvailableFreeSpace / 1_073_741_824.0, totalGb = drive.TotalSize / 1_073_741_824.0 },
            uptimeHours = Environment.TickCount64 / 3_600_000.0
        };
        return JsonSerializer.Serialize(report, ContractJson.Options);
    }
}

public sealed class RebootHandler(IProcessRunner runner, IAtmActivityProbe activity) : IRemoteCommandHandler
{
    public RemoteCommandType Type => RemoteCommandType.RebootAtm;

    public async Task<string> HandleAsync(RemoteCommand c, CancellationToken ct)
    {
        await CommandParams.EnsureIdleAsync(activity, c, ct).ConfigureAwait(false);
        var r = await runner.RunAsync("shutdown.exe",
            ["/r", "/t", "60", "/c", $"ATM Nexus remote reboot {c.CommandId:N}", "/d", "p:4:1"], TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        return r.ExitCode == 0 ? "reboot scheduled in 60 seconds" : throw new InvalidOperationException($"shutdown.exe exit {r.ExitCode}: {r.StdErr}");
    }
}

public sealed class ShutdownHandler(IProcessRunner runner, IAtmActivityProbe activity) : IRemoteCommandHandler
{
    public RemoteCommandType Type => RemoteCommandType.ShutdownAtm;

    public async Task<string> HandleAsync(RemoteCommand c, CancellationToken ct)
    {
        await CommandParams.EnsureIdleAsync(activity, c, ct).ConfigureAwait(false);
        var r = await runner.RunAsync("shutdown.exe",
            ["/s", "/t", "60", "/c", $"ATM Nexus remote shutdown {c.CommandId:N}", "/d", "p:4:1"], TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        return r.ExitCode == 0 ? "shutdown scheduled in 60 seconds" : throw new InvalidOperationException($"shutdown.exe exit {r.ExitCode}: {r.StdErr}");
    }
}
