using System.Threading.Channels;
using AtmNexus.Agent.Health;

namespace AtmNexus.Agent;

public interface IDeviceEventBus
{
    bool TryPublish(DeviceEvent e);
    IAsyncEnumerable<DeviceEvent> ReadAllAsync(CancellationToken ct);
}

/// <summary>Bounded, drop-oldest buffer: XFS callbacks must never block on downstream processing.</summary>
public sealed class ChannelDeviceEventBus : IDeviceEventBus
{
    private readonly Channel<DeviceEvent> _ch = Channel.CreateBounded<DeviceEvent>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    public bool TryPublish(DeviceEvent e) => _ch.Writer.TryWrite(e);
    public IAsyncEnumerable<DeviceEvent> ReadAllAsync(CancellationToken ct) => _ch.Reader.ReadAllAsync(ct);
}

public interface IUploadSignal
{
    void Trigger();
    Task WaitAsync(TimeSpan timeout, CancellationToken ct);
}

public sealed class UploadSignal : IUploadSignal
{
    private readonly SemaphoreSlim _sem = new(0, 1);
    public void Trigger() { try { _sem.Release(); } catch (SemaphoreFullException) { } }
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await _sem.WaitAsync(timeout, ct).ConfigureAwait(false);
}

/// <summary>Thread-safe latest-known state of the ATM; the single source for health scoring and telemetry snapshots.</summary>
public sealed class AgentStateStore(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceStatus> _devices = new();
    private readonly Dictionary<string, IReadOnlyList<CashUnit>> _cash = new();
    private readonly List<AlarmRecord> _alarms = new();
    private readonly Dictionary<string, DateTimeOffset> _conditions = new();
    private ApplicationStatus _app = ApplicationStatus.Unknown;
    private NetworkStatus _net = NetworkStatus.Unknown;
    private bool _patchCompliant = true;

    public HealthScoreResult? LastHealth { get; private set; }
    public DateTimeOffset LastHealthAt { get; private set; }

    public void UpdateDevice(DeviceStatus s) { lock (_gate) _devices[s.LogicalName] = s; }
    public DeviceStatus? GetDevice(string logicalName) { lock (_gate) return _devices.GetValueOrDefault(logicalName); }
    public void UpdateCash(string device, IReadOnlyList<CashUnit> units) { lock (_gate) _cash[device] = units; }
    public IReadOnlyList<CashUnit>? GetCash(string device) { lock (_gate) return _cash.GetValueOrDefault(device); }
    public void SetApplication(ApplicationStatus a) { lock (_gate) _app = a; }
    public void UpdateApplication(Func<ApplicationStatus, ApplicationStatus> update) { lock (_gate) _app = update(_app); }
    public void SetNetwork(NetworkStatus n) { lock (_gate) _net = n; }
    public void SetPatchCompliance(bool ok) { lock (_gate) _patchCompliant = ok; }
    public void SetHealth(HealthScoreResult r) { lock (_gate) { LastHealth = r; LastHealthAt = _clock.GetUtcNow(); } }

    public void RecordEvent(DeviceEvent e)
    {
        lock (_gate)
        {
            _alarms.Add(new AlarmRecord(e.Type, e.Severity, e.At));
            var cutoff = _clock.GetUtcNow().AddHours(-24);
            _alarms.RemoveAll(a => a.At < cutoff);
            if (_alarms.Count > 500) _alarms.RemoveRange(0, _alarms.Count - 500);

            // Latched conditions are cleared by their counterpart event.
            switch (e.Type)
            {
                case DeviceEventType.SafeDoorOpen: _conditions["safeDoor"] = e.At; break;
                case DeviceEventType.SafeDoorClosed: _conditions.Remove("safeDoor"); break;
                case DeviceEventType.CabinetDoorOpen: _conditions["cabinet"] = e.At; break;
                case DeviceEventType.CabinetDoorClosed: _conditions.Remove("cabinet"); break;
                case DeviceEventType.TamperDetected: _conditions["tamper"] = e.At; break;
                case DeviceEventType.FraudAttempt: _conditions["fraud"] = e.At; break;
            }
        }
    }

    public IReadOnlyList<DeviceStatus> Devices() { lock (_gate) return _devices.Values.ToList(); }
    public IReadOnlyList<CashUnit> AllCashUnits() { lock (_gate) return _cash.Values.SelectMany(x => x).ToList(); }

    public HealthInputs Snapshot()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            bool Attr(string key, string value) => _devices.Values.Any(d => d.Attributes.GetValueOrDefault(key, "") == value);
            var security = new SecurityStatus(
                TamperActive: _conditions.ContainsKey("tamper") || Attr(DeviceAttributes.Tamper, DeviceAttributes.Active),
                SafeDoorOpen: _conditions.ContainsKey("safeDoor") || Attr(DeviceAttributes.SafeDoor, DeviceAttributes.Open),
                CabinetOpen: _conditions.ContainsKey("cabinet") || Attr(DeviceAttributes.CabinetDoor, DeviceAttributes.Open),
                FraudAttemptRecent: _conditions.TryGetValue("fraud", out var at) && now - at < TimeSpan.FromHours(1),
                PatchCompliant: _patchCompliant);

            return new HealthInputs(now, _devices.Values.ToList(), _cash.Values.SelectMany(x => x).ToList(),
                _app, _net, security, _alarms.ToList());
        }
    }
}
