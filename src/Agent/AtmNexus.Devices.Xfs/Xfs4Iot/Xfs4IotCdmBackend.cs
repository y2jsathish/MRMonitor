using System.Text.Json;

namespace AtmNexus.Devices.Xfs.Xfs4Iot;

/// <summary>XFS4IoT implementor of the CDM bridge. Same CashDispenserService business logic as CEN/XFS 3.x.</summary>
public sealed class Xfs4IotCdmBackend(Xfs4IotClient client, DeviceDescriptor descriptor, ILogger<Xfs4IotCdmBackend> log) : ICdmBackend
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(15), Long = TimeSpan.FromSeconds(60);

    public string AdapterName => "xfs4iot";
    public event EventHandler<DeviceEvent>? EventRaised;

    public async Task OpenAsync(CancellationToken ct)
    {
        if (!client.IsConnected) await client.ConnectAsync(ct).ConfigureAwait(false);
        client.EventReceived += OnEvent;
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        client.EventReceived -= OnEvent;
        await client.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<DeviceStatus> GetStatusAsync(CancellationToken ct)
    {
        var p = await client.SendCommandAsync("Common.Status", null, Short, ct).ConfigureAwait(false);
        var attrs = new Dictionary<string, string>();
        var state = DeviceState.Unknown;
        if (p.TryGetProperty("common", out var common) && common.TryGetProperty("device", out var dev))
            state = MapDevice(dev.GetString());
        if (p.TryGetProperty("cashManagement", out var cm) && cm.TryGetProperty("safeDoor", out var sd))
            attrs[DeviceAttributes.SafeDoor] = sd.GetString() ?? "unknown";
        if (p.TryGetProperty("cashDispenser", out var cd) && cd.TryGetProperty("intermediateStacker", out var st))
            attrs["intermediateStacker"] = st.GetString() ?? "unknown";
        return new DeviceStatus(descriptor.LogicalName, descriptor.Class, state, attrs, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<CashUnit>> GetCashUnitsAsync(CancellationToken ct)
    {
        var p = await client.SendCommandAsync("CashManagement.GetCashUnitInfo", null, Short, ct).ConfigureAwait(false);
        var units = new List<CashUnit>();
        if (!p.TryGetProperty("units", out var map) || map.ValueKind != JsonValueKind.Object) return units;

        int n = 0;
        foreach (var u in map.EnumerateObject())      // XFS4IoT keys units as "unit1", "unit2", ...
        {
            n++;
            var v = u.Value;
            units.Add(new CashUnit(
                Number: n, UnitId: u.Name, Name: u.Name,
                Type: MapType(Str(v, "type")),
                Currency: Str(v, "currency") ?? "",
                Denomination: (int)Num(v, "value"),
                InitialCount: (int)Num(v, "initialCount"),
                Count: (int)Num(v, "count"),
                RejectCount: (int)Num(v, "rejectCount"),
                Minimum: (int)Num(v, "minimum"),
                Maximum: (int)Num(v, "maximum"),
                Status: MapStatus(Str(v, "status")),
                AppLocked: v.TryGetProperty("appLock", out var al) && al.ValueKind == JsonValueKind.True));
        }
        return units;
    }

    public Task DispenseAsync(DispenseRequest r, CancellationToken ct) =>
        client.SendCommandAsync("CashDispenser.Dispense", new
        {
            denomination = new { currencies = new Dictionary<string, long> { [r.Currency] = r.Amount } },
            present = r.Present,
            position = "default"
        }, Long, ct);

    public Task PresentAsync(CancellationToken ct) => client.SendCommandAsync("CashDispenser.Present", new { position = "default" }, Long, ct);
    public Task RejectAsync(CancellationToken ct) => client.SendCommandAsync("CashDispenser.Reject", null, Long, ct);
    public Task RetractAsync(CancellationToken ct) => client.SendCommandAsync("CashManagement.Retract", null, Long, ct);
    public Task ResetAsync(CancellationToken ct) => client.SendCommandAsync("CashManagement.Reset", null, Long, ct);

    // ---- unsolicited events -> domain events ----
    private void OnEvent(string name, JsonElement payload)
    {
        try
        {
            DeviceEvent? ev = name switch
            {
                "CashManagement.CashUnitThresholdEvent" => Threshold(payload),
                "Common.StatusChangedEvent" => StatusChanged(payload),
                _ => null
            };
            if (ev is not null) EventRaised?.Invoke(this, ev);
        }
        catch (Exception ex) { log.LogWarning(ex, "Failed to map XFS4IoT event {Name}", name); }
    }

    private DeviceEvent Threshold(JsonElement p)
    {
        string status = Str(p, "status") ?? "unknown";
        var (type, sev) = status switch
        {
            "empty" => (DeviceEventType.CashEmpty, AlertSeverity.Critical),
            "low" => (DeviceEventType.CashLow, AlertSeverity.Major),
            "high" or "full" => (DeviceEventType.CashHigh, AlertSeverity.Warning),
            _ => (DeviceEventType.CashUnitChanged, AlertSeverity.Info)
        };
        return DeviceEvent.Create(descriptor.LogicalName, descriptor.Class, type, sev, $"Cash unit {Str(p, "unit")} {status}",
            data: new Dictionary<string, string> { ["unit"] = Str(p, "unit") ?? "", ["status"] = status });
    }

    private DeviceEvent? StatusChanged(JsonElement p)
    {
        if (p.TryGetProperty("cashManagement", out var cm) && cm.TryGetProperty("safeDoor", out var sd))
        {
            bool open = sd.GetString() == "open";
            return DeviceEvent.Create(descriptor.LogicalName, descriptor.Class,
                open ? DeviceEventType.SafeDoorOpen : DeviceEventType.SafeDoorClosed,
                open ? AlertSeverity.Critical : AlertSeverity.Info, open ? "Safe door opened" : "Safe door closed");
        }
        if (p.TryGetProperty("common", out var c) && c.TryGetProperty("device", out var d))
        {
            var state = MapDevice(d.GetString());
            return DeviceEvent.Create(descriptor.LogicalName, descriptor.Class, DeviceEventType.DeviceStatusChanged,
                state is DeviceState.Online or DeviceState.Busy ? AlertSeverity.Info : AlertSeverity.Major, $"Device state {state}");
        }
        return null;
    }

    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static DeviceState MapDevice(string? s) => s switch
    {
        "online" => DeviceState.Online, "offline" => DeviceState.Offline, "powerOff" => DeviceState.PowerOff,
        "noDevice" => DeviceState.NoDevice, "hardwareError" => DeviceState.HardwareError, "userError" => DeviceState.UserError,
        "deviceBusy" => DeviceState.Busy, "fraudAttempt" or "potentialFraud" => DeviceState.FraudAttempt, _ => DeviceState.Unknown
    };

    private static CashUnitStatus MapStatus(string? s) => s switch
    {
        "ok" => CashUnitStatus.Ok, "full" => CashUnitStatus.Full, "high" => CashUnitStatus.High, "low" => CashUnitStatus.Low,
        "empty" => CashUnitStatus.Empty, "inoperative" => CashUnitStatus.Inoperative, "missing" => CashUnitStatus.Missing,
        "noValue" => CashUnitStatus.NoValue, "noReference" => CashUnitStatus.NoReference, "manipulated" => CashUnitStatus.Manipulated,
        _ => CashUnitStatus.Unknown
    };

    private static CashUnitType MapType(string? s) => s switch
    {
        "billCassette" or "dispense" => CashUnitType.Dispense, "rejectCassette" => CashUnitType.Reject,
        "retractCassette" => CashUnitType.Retract, "recycling" => CashUnitType.Recycle, "coinDispenser" => CashUnitType.Coin,
        _ => CashUnitType.Unknown
    };

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
