using System.Collections.Concurrent;

namespace AtmNexus.Agent.Devices;

/// <summary>Finds logical devices on the ATM itself (e.g. the XFS LOGICAL_SERVICES registry hive).</summary>
public interface IDeviceDiscovery
{
    IReadOnlyList<DeviceDescriptor> Discover();
}

public sealed class DeviceManagerOptions
{
    public List<DeviceDescriptor> Devices { get; set; } = new();
    /// <summary>Merge discovered logical services with <see cref="Devices"/> (explicit configuration wins by LogicalName).</summary>
    public bool AutoDiscover { get; set; }
    public TimeSpan StatusTimeout { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Plugin host: resolves one <see cref="IDeviceAdapterFactory"/> per descriptor, opens devices (tolerating failures,
/// retrying on every poll), and fans device events into the agent event bus.
/// ATM Agent -> DeviceManager -> adapter (xfs3 | xfs4iot | simulator) -> service provider -> hardware.
/// </summary>
public sealed class DeviceManager(
    IOptions<DeviceManagerOptions> options,
    IEnumerable<IDeviceAdapterFactory> factories,
    IEnumerable<IDeviceDiscovery> discoveries,
    IServiceProvider services,
    IDeviceEventBus bus,
    ILogger<DeviceManager> log) : IDeviceManager
{
    private readonly ConcurrentDictionary<string, IATMDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _opened = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceManagerOptions _opt = options.Value;
    private int _started;

    public IReadOnlyCollection<IATMDevice> Devices => _devices.Values.ToList();

    public async Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var descriptors = _opt.Devices.ToList();
        if (_opt.AutoDiscover)
        {
            foreach (var found in discoveries.SelectMany(x => x.Discover()))
                if (!descriptors.Any(x => x.LogicalName.Equals(found.LogicalName, StringComparison.OrdinalIgnoreCase)))
                {
                    log.LogInformation("Discovered XFS logical service {LogicalName} ({Class})", found.LogicalName, found.Class);
                    descriptors.Add(found);
                }
        }

        foreach (var d in descriptors)
        {
            var factory = factories.FirstOrDefault(f => f.CanCreate(d));
            if (factory is null)
            {
                log.LogError("No adapter factory for device {LogicalName} (adapter={Adapter}, class={Class})", d.LogicalName, d.Adapter, d.Class);
                continue;
            }
            try
            {
                var dev = factory.Create(d, services);
                dev.EventRaised += (_, e) => bus.TryPublish(e);
                _devices[d.LogicalName] = dev;
                await EnsureOpenAsync(dev, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to create device {LogicalName}", d.LogicalName);
            }
        }
    }

    private async Task<bool> EnsureOpenAsync(IATMDevice dev, CancellationToken ct)
    {
        if (_opened.TryGetValue(dev.LogicalName, out var ok) && ok) return true;
        try
        {
            await dev.OpenAsync(ct).ConfigureAwait(false);
            _opened[dev.LogicalName] = true;
            log.LogInformation("Device {LogicalName} ({Class}/{Adapter}) opened", dev.LogicalName, dev.Class, dev.AdapterName);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _opened[dev.LogicalName] = false;
            log.LogWarning(ex, "Device {LogicalName} could not be opened; will retry on next poll", dev.LogicalName);
            return false;
        }
    }

    public T? Get<T>(string logicalName) where T : class, IATMDevice =>
        _devices.TryGetValue(logicalName, out var d) ? d as T : null;

    public IEnumerable<T> GetAll<T>() where T : class, IATMDevice => _devices.Values.OfType<T>();

    public async Task<IReadOnlyList<DeviceStatus>> PollAllAsync(CancellationToken ct)
    {
        var tasks = _devices.Values.Select(d => PollOneAsync(d, ct));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<DeviceStatus> PollOneAsync(IATMDevice dev, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_opt.StatusTimeout);
        try
        {
            if (!await EnsureOpenAsync(dev, cts.Token).ConfigureAwait(false))
                return Offline(dev, "open-failed");
            return await dev.GetStatusAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Status poll of {LogicalName} timed out after {Timeout}", dev.LogicalName, _opt.StatusTimeout);
            _opened[dev.LogicalName] = false;      // force re-open: a hung SP often needs a fresh handle
            return Offline(dev, "timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Status poll of {LogicalName} failed", dev.LogicalName);
            return Offline(dev, ex.GetType().Name);
        }
    }

    private static DeviceStatus Offline(IATMDevice d, string reason) =>
        new(d.LogicalName, d.Class, DeviceState.Offline, new Dictionary<string, string> { ["error"] = reason }, DateTimeOffset.UtcNow);

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _devices.Values)
        {
            try { await d.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { log.LogDebug(ex, "dispose {LogicalName} failed", d.LogicalName); }
        }
    }
}
