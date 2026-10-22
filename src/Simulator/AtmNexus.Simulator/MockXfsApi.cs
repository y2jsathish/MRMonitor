using System.Collections.Concurrent;

namespace AtmNexus.Simulator;

public sealed class SimulatedCashUnit
{
    public ushort Number { get; init; }
    public ushort XfsType { get; init; } = 3;          // 3 = bill cassette, 2 = reject cassette
    public string Name { get; init; } = "";
    public string UnitId { get; init; } = "";
    public string Currency { get; init; } = "USD";
    public uint Value { get; init; }
    public uint Initial { get; init; }
    public uint Count { get; set; }
    public uint Reject { get; set; }
    public uint Minimum { get; init; } = 100;
    public uint Maximum { get; init; } = 2000;

    public ushort Status => XfsType != 3 ? (ushort)0 : Count == 0 ? (ushort)4 : Count <= Minimum ? (ushort)3 : Count >= Maximum ? (ushort)1 : (ushort)0;
}

/// <summary>Physical state of the simulated ATM. All scenario commands mutate this; MockXfsApi renders it as XFS structures.</summary>
public sealed class SimulatedAtm
{
    public object Gate { get; } = new();
    public List<SimulatedCashUnit> Units { get; } =
    [
        new() { Number = 1, Name = "Cassette 1", UnitId = "CASS1", Value = 20, Initial = 1000, Count = 1000 },
        new() { Number = 2, Name = "Cassette 2", UnitId = "CASS2", Value = 50, Initial = 800, Count = 800 },
        new() { Number = 3, Name = "Cassette 3", UnitId = "CASS3", Value = 100, Initial = 500, Count = 500 },
        new() { Number = 4, XfsType = 2, Name = "Reject bin", UnitId = "REJ", Value = 0, Initial = 0, Count = 0, Minimum = 0, Maximum = 400 }
    ];

    public bool SafeDoorOpen, CabinetOpen, TamperActive, VibrationActive, HeatActive;
    public bool CardJammed, CardPresent;
    public ushort RetainedCards;
    public ushort PaperLevel;            // 0 full, 1 low, 2 out
    public bool PaperJammed;
    public ushort EppState;              // 0 ready, 6 tampered
    public ushort DeviceState;           // 0 online, 4 hardware error
    public string TestPan = "4111111111111111";    // Visa test PAN
    public string TestTrack2 => $";{TestPan}=29122011234567890?";

    /// <summary>Greedy largest-first payout from cassettes (caller holds <see cref="Gate"/>). Returns false if the amount cannot be composed.</summary>
    public bool TryDispense(string currency, uint amount, List<SimulatedCashUnit> crossedThreshold)
    {
        uint remaining = amount;
        var plan = new Dictionary<SimulatedCashUnit, uint>();
        foreach (var u in Units.Where(u => u.XfsType == 3 && u.Currency == currency && u.Count > 0 && u.Value > 0).OrderByDescending(u => u.Value))
        {
            uint n = Math.Min(remaining / u.Value, u.Count);
            if (n > 0) { plan[u] = n; remaining -= n * u.Value; }
        }
        if (remaining != 0) return false;
        foreach (var (unit, n) in plan)
        {
            ushort before = unit.Status;
            unit.Count -= n;
            if (unit.Status != before && unit.Status is 3 or 4) crossedThreshold.Add(unit);
        }
        return true;
    }
    public bool ChipFailure;             // chip unreadable while the magstripe says "chip card" => technical-fallback / skimming indicator
    public byte[] TestAtr => [0x3B, 0x65, 0x00, 0x00, 0x20, 0x63, 0xCB, 0xA3, 0xA0];
}

/// <summary>
/// Drop-in <see cref="IXfsApi"/> for tests and the simulator: renders <see cref="SimulatedAtm"/> as XFS status/cash-unit
/// payloads, executes commands against it, and raises WFS_*_EVENTs exactly like an SP would. The agent cannot tell the difference.
/// </summary>
public sealed class MockXfsApi(SimulatedAtm atm, SiuSensorMap? sensorMap = null) : IXfsApi
{
    private readonly SiuSensorMap _map = sensorMap ?? SiuSensorMap.Default;
    private readonly ConcurrentDictionary<ushort, (string Name, DeviceClass Class)> _handles = new();
    private int _next;

    public event Action<XfsApiEvent>? EventReceived;

    public void StartUp() { }

    public XfsOpenInfo Open(string logicalName, DeviceClass deviceClass, TimeSpan timeout)
    {
        ushort h = (ushort)Interlocked.Increment(ref _next);
        _handles[h] = (logicalName, deviceClass);
        return new XfsOpenInfo(h, 0x0003, 0x0003, $"Mock SP for {logicalName}");
    }

    public int Close(ushort handle) => _handles.TryRemove(handle, out _) ? 0 : -1;
    public int Register(ushort handle, XfsEventClass classes) => 0;
    public int Deregister(ushort handle, XfsEventClass classes) => 0;
    public int Lock(ushort handle, TimeSpan timeout) => 0;
    public int Unlock(ushort handle) => 0;

    // ------------------------------------------------------------------ GetInfo
    public XfsResponse GetInfo(ushort handle, uint category, object? query, TimeSpan timeout)
    {
        lock (atm.Gate)
        {
            return category switch
            {
                XfsIds.Cdm.InfoStatus => new XfsResponse(0, Status(("fwDevice", atm.DeviceState), ("fwSafeDoor", atm.SafeDoorOpen ? (ushort)1 : (ushort)2), ("fwDispenser", 0), ("fwIntermediateStacker", 0))),
                XfsIds.Cdm.InfoCashUnitInfo => new XfsResponse(0, new XfsCashUnitInfoData(atm.Units.Select(ToData).ToList())),
                XfsIds.Idc.InfoStatus => new XfsResponse(0, Status(("fwDevice", 0), ("fwMedia", atm.CardJammed ? (ushort)2 : atm.CardPresent ? (ushort)0 : (ushort)1),
                    ("fwRetainBin", atm.RetainedCards >= 50 ? (ushort)1 : (ushort)0), ("fwSecurity", 0), ("usCards", atm.RetainedCards), ("fwChipPower", 0))),
                XfsIds.Ptr.InfoStatus => new XfsResponse(0, PrinterStatus()),
                XfsIds.Pin.InfoStatus => new XfsResponse(0, Status(("fwDevice", 0), ("fwEncStat", atm.EppState))),
                XfsIds.Siu.InfoStatus => new XfsResponse(0, SiuStatus()),
                _ => new XfsResponse(-1, null)
            };
        }
    }

    private static XfsStatusData Status(params (string Name, ushort Value)[] fields) =>
        new(fields.ToDictionary(f => f.Name, f => f.Value), new Dictionary<string, ushort[]>(), null);

    private XfsStatusData PrinterStatus()
    {
        var paper = new ushort[16];
        paper[0] = atm.PaperJammed ? (ushort)5 : atm.PaperLevel;
        for (int i = 1; i < paper.Length; i++) paper[i] = 3;   // not supported
        return new XfsStatusData(new Dictionary<string, ushort> { ["fwDevice"] = 0, ["fwMedia"] = 1, ["fwToner"] = 3 },
            new Dictionary<string, ushort[]> { ["fwPaper"] = paper }, null);
    }

    private XfsStatusData SiuStatus()
    {
        var sensors = new ushort[16];
        var doors = new ushort[16];
        foreach (var e in _map.All)
        {
            bool active = ReadSensor(e.Kind);
            var arr = e.PortType == 2 ? doors : sensors;
            arr[e.PortIndex] = active ? e.ActiveMask : (e.PortType == 2 ? (ushort)0x0001 : (ushort)0x0002);
        }
        return new XfsStatusData(new Dictionary<string, ushort> { ["fwDevice"] = 0 },
            new Dictionary<string, ushort[]> { ["fwSensors"] = sensors, ["fwDoors"] = doors }, null);
    }

    private bool ReadSensor(SensorKind k) => k switch
    {
        SensorKind.SafeDoor => atm.SafeDoorOpen,
        SensorKind.CabinetDoor => atm.CabinetOpen,
        SensorKind.Tamper or SensorKind.InternalTamper => atm.TamperActive,
        SensorKind.Vibration => atm.VibrationActive,
        SensorKind.Temperature => atm.HeatActive,
        _ => false
    };

    private static XfsCashUnitData ToData(SimulatedCashUnit u) =>
        new(u.Number, u.XfsType, u.Name, u.UnitId, u.Currency, u.Value, u.Initial, u.Count, u.Reject, u.Minimum, u.Maximum, false, u.Status);

    // ------------------------------------------------------------------ Execute
    public XfsResponse Execute(ushort handle, uint command, object? data, TimeSpan timeout)
    {
        var events = new List<Action>();
        XfsResponse response;
        lock (atm.Gate)
        {
            response = command switch
            {
                XfsIds.Cdm.CmdDispense when data is XfsCdmDispenseData d => Dispense(d, events),
                XfsIds.Cdm.CmdPresent or XfsIds.Cdm.CmdReject or XfsIds.Cdm.CmdRetract or XfsIds.Cdm.CmdReset => new XfsResponse(0, null),
                XfsIds.Idc.CmdReadRawData when data is ushort flags => ReadCard(flags),
                XfsIds.Idc.CmdEjectCard => Do(() => atm.CardPresent = false),
                XfsIds.Idc.CmdRetainCard => Do(() => { atm.CardPresent = false; atm.RetainedCards++; }),
                XfsIds.Idc.CmdReset => Do(() => atm.CardJammed = false),
                XfsIds.Ptr.CmdReset => Do(() => atm.PaperJammed = false),
                XfsIds.Pin.CmdReset or XfsIds.Siu.CmdReset or XfsIds.Siu.CmdEnableEvents => new XfsResponse(0, null),
                _ => new XfsResponse(-1, null)
            };
        }
        foreach (var e in events) e();     // raise outside the lock, like a real SP posting messages
        return response;
    }

    private static XfsResponse Do(Action a) { a(); return new XfsResponse(0, null); }

    private XfsResponse ReadCard(ushort flags)
    {
        if (atm.CardJammed) return new XfsResponse(-1002, null);
        if (!atm.CardPresent) return new XfsResponse(-1003, null);
        var items = new List<XfsCardDataItem>();
        if ((flags & XfsIds.Idc.Track2) != 0) items.Add(new XfsCardDataItem(XfsIds.Idc.Track2, 0, System.Text.Encoding.ASCII.GetBytes(atm.TestTrack2)));
        if ((flags & XfsIds.Idc.Chip) != 0)
            items.Add(atm.ChipFailure ? new XfsCardDataItem(XfsIds.Idc.Chip, 1, []) : new XfsCardDataItem(XfsIds.Idc.Chip, 0, atm.TestAtr));
        return new XfsResponse(0, new XfsCardDataResult(items));
    }

    private XfsResponse Dispense(XfsCdmDispenseData d, List<Action> events)
    {
        if (atm.DeviceState != 0) return new XfsResponse(-1004, null);
        var crossed = new List<SimulatedCashUnit>();
        if (!atm.TryDispense(d.Currency, d.Amount, crossed)) return new XfsResponse(-1001, null);   // cannot compose the amount
        foreach (var unit in crossed)
        {
            var snapshot = ToData(unit);
            events.Add(() => Raise(XfsEventScope.Cdm, XfsEventKind.User, 201, snapshot));            // WFS_USRE_CDM_CASHUNITTHRESHOLD
        }
        return new XfsResponse(0, null);
    }

    // ------------------------------------------------------------------ event injection
    /// <summary>Raises an XFS event on every open service handle of the given scope (null scope = all handles).</summary>
    public void Raise(XfsEventScope? scope, XfsEventKind kind, uint eventId, object? data = null, int hResult = 0)
    {
        foreach (var (handle, info) in _handles)
        {
            if (scope is not null && XfsEventCatalog.ScopeOf(info.Class) != scope) continue;
            EventReceived?.Invoke(new XfsApiEvent(kind, handle, eventId, hResult, data, DateTimeOffset.UtcNow));
            if (scope is null) continue;
            break;                                                    // one handle per scope is enough
        }
    }

    public void Dispose() { }
}
