namespace AtmNexus.Agent.Platform;

public enum WinServiceState { NotFound, Stopped, StartPending, StopPending, Running, Paused, Unknown }

/// <summary>Windows Service Control Manager access (implemented with System.ServiceProcess in the Agent exe).</summary>
public interface IWindowsServiceController
{
    Task<WinServiceState> GetStateAsync(string serviceName, CancellationToken ct);
    Task RestartAsync(string serviceName, TimeSpan timeout, CancellationToken ct);
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Runs an executable WITHOUT a shell: file + argument list only. No string concatenation of user input.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
    bool IsRunning(string processName);
}

public sealed record WindowsEventRecord(string Log, string Provider, int EventId, AlertSeverity Severity, DateTimeOffset At, string Message);

public interface IWindowsEventSource
{
    /// <summary>Returns Error/Critical (and selected Warning) events newer than the persisted bookmark.</summary>
    Task<IReadOnlyList<WindowsEventRecord>> ReadNewAsync(CancellationToken ct);
}

public sealed record SoftwareItem(string Name, string Version, string? Publisher, DateOnly? InstallDate);
public sealed record PatchItem(string HotFixId, string? Description, DateOnly? InstalledOn);

public interface ISoftwareInventory { Task<IReadOnlyList<SoftwareItem>> ListAsync(CancellationToken ct); }
public interface IPatchInventory { Task<IReadOnlyList<PatchItem>> ListAsync(CancellationToken ct); }

public sealed record NetworkProbeResult(bool Reachable, double LatencyMs, double PacketLossPct);
public interface INetworkProbe { Task<NetworkProbeResult> ProbeAsync(CancellationToken ct); }

public interface ITemperatureProbe { Task<double?> ReadCelsiusAsync(CancellationToken ct); }

/// <summary>True when no customer transaction is in progress (used to gate reboot/restart commands).</summary>
public interface IAtmActivityProbe { Task<bool> IsIdleAsync(CancellationToken ct); }

public interface ILogBundleUploader
{
    Task<string> BundleAndUploadAsync(IEnumerable<string> directories, TimeSpan lookback, string label, CancellationToken ct);
}

public interface IAgentTokenProvider { Task<string?> GetTokenAsync(CancellationToken ct); }
