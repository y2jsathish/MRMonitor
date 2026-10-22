using System.Collections.Concurrent;
using AtmNexus.Agent;
using AtmNexus.Agent.Platform;

namespace AtmNexus.Simulator;

/// <summary>In-memory stand-ins for the Windows-only services so the real agent pipeline runs on any OS.</summary>
public sealed class FakeServiceController : IWindowsServiceController
{
    public ConcurrentDictionary<string, WinServiceState> States { get; } = new();
    public List<string> Restarted { get; } = new();

    public Task<WinServiceState> GetStateAsync(string serviceName, CancellationToken ct) =>
        Task.FromResult(States.GetValueOrDefault(serviceName, WinServiceState.Running));

    public Task RestartAsync(string serviceName, TimeSpan timeout, CancellationToken ct)
    {
        States[serviceName] = WinServiceState.Running;
        lock (Restarted) Restarted.Add(serviceName);
        return Task.CompletedTask;
    }
}

public sealed class FakeProcessRunner : IProcessRunner
{
    public List<(string File, string[] Args)> Calls { get; } = new();
    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        lock (Calls) Calls.Add((fileName, arguments.ToArray()));
        return Task.FromResult(new ProcessResult(0, "", ""));
    }
    public bool IsRunning(string processName) => true;
}

public sealed class FakeNetworkProbe : INetworkProbe
{
    public NetworkProbeResult Result { get; set; } = new(true, 35, 0);
    public Task<NetworkProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(Result);
}

public sealed class EmptyWindowsEventSource : IWindowsEventSource
{
    public Task<IReadOnlyList<WindowsEventRecord>> ReadNewAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WindowsEventRecord>>([]);
}

public sealed class FakeSoftwareInventory : ISoftwareInventory
{
    public Task<IReadOnlyList<SoftwareItem>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SoftwareItem>>(
    [
        new("Simulated ATM Application", "5.4.2", "ATM Nexus Lab", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-40))),
        new("Simulated XFS Manager", "3.30", "ATM Nexus Lab", null)
    ]);
}

public sealed class FakePatchInventory : IPatchInventory
{
    public Task<IReadOnlyList<PatchItem>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PatchItem>>(
        [new("KB5099999", "Security Update", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-20)))]);
}

public sealed class SettableIdleProbe : IAtmActivityProbe
{
    public bool Idle { get; set; } = true;
    public Task<bool> IsIdleAsync(CancellationToken ct) => Task.FromResult(Idle);
}

public sealed class NoopLogBundleUploader : ILogBundleUploader
{
    public Task<string> BundleAndUploadAsync(IEnumerable<string> directories, TimeSpan lookback, string label, CancellationToken ct) =>
        Task.FromResult($"sim://logs/{label}");
}

public sealed class NullTokenProvider : IAgentTokenProvider
{
    public Task<string?> GetTokenAsync(CancellationToken ct) => Task.FromResult<string?>(null);
}
