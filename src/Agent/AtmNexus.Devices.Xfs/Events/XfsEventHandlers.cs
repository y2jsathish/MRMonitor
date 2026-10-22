namespace AtmNexus.Devices.Xfs;

public sealed record SiuSensorEntry(ushort PortType, ushort PortIndex, SensorKind Kind, string Name, ushort ActiveMask);

/// <summary>
/// Maps WFS_SRVE_SIU_PORT_STATUS (port type/index/status) to a sensor and defines which status bits mean "active".
/// Port types: 1=sensors 2=doors. Doors: status bit 0x0002 = open. Sensors: 0x0001 = present/triggered.
/// Vendors wire sensors differently - override per vendor profile (e.g. Hyosung uses index 6+ for vibration).
/// </summary>
public sealed class SiuSensorMap(IEnumerable<SiuSensorEntry>? entries = null)
{
    public static readonly SiuSensorMap Default = new();

    private readonly Dictionary<(ushort, ushort), SiuSensorEntry> _map =
        (entries ?? DefaultEntries()).ToDictionary(e => (e.PortType, e.PortIndex));

    public SiuSensorEntry? Find(ushort portType, ushort index) => _map.GetValueOrDefault((portType, index));
    public IEnumerable<SiuSensorEntry> All => _map.Values;

    private static IEnumerable<SiuSensorEntry> DefaultEntries() =>
    [
        new(2, 0, SensorKind.CabinetDoor, "Cabinet door", 0x0002),
        new(2, 1, SensorKind.SafeDoor, "Safe door", 0x0002),
        new(2, 2, SensorKind.VandalShield, "Vandal shield", 0x0002),
        new(1, 0, SensorKind.OperatorSwitch, "Operator switch", 0x0001),
        new(1, 1, SensorKind.Tamper, "Tamper sensor", 0x0001),
        new(1, 2, SensorKind.InternalTamper, "Internal tamper sensor", 0x0001),
        new(1, 3, SensorKind.Vibration, "Seismic/vibration sensor", 0x0001),
        new(1, 4, SensorKind.Temperature, "Heat sensor", 0x0001),
        new(1, 5, SensorKind.Proximity, "Proximity sensor", 0x0001),
    ];
}

public interface IXfsEventHandler
{
    XfsEventKind Kind { get; }
    DeviceEvent? Handle(XfsEvent e);
}

public abstract class CatalogEventHandler(XfsEventCatalog catalog) : IXfsEventHandler
{
    public abstract XfsEventKind Kind { get; }

    public DeviceEvent? Handle(XfsEvent e)
    {
        var def = catalog.Find(e.Class, Kind, e.EventId);
        if (def is null)
        {
            return DeviceEvent.Create(e.LogicalName, e.Class, DeviceEventType.Unknown, AlertSeverity.Info,
                $"Unmapped XFS {Kind} event {e.EventId}", e.At,
                new Dictionary<string, string> { ["xfs.kind"] = Kind.ToString(), ["xfs.eventId"] = e.EventId.ToString() });
        }

        var data = new Dictionary<string, string>
        {
            ["xfs.event"] = def.Name,
            ["xfs.eventId"] = e.EventId.ToString(),
            ["xfs.hResult"] = e.HResult.ToString()
        };
        var ev = DeviceEvent.Create(e.LogicalName, e.Class, def.Type, def.Severity, def.Name, e.At, data);
        return Enrich(e, def, ev, data);
    }

    protected virtual DeviceEvent Enrich(XfsEvent e, XfsEventDefinition def, DeviceEvent ev, Dictionary<string, string> data) => ev;
}

/// <summary>WFS_EXECUTE_EVENT: intermediate events raised while a command is executing (card inserted, cash unit error, ...).</summary>
public sealed class ExecuteEventHandler(XfsEventCatalog catalog) : CatalogEventHandler(catalog)
{
    public override XfsEventKind Kind => XfsEventKind.Execute;

    protected override DeviceEvent Enrich(XfsEvent e, XfsEventDefinition def, DeviceEvent ev, Dictionary<string, string> data)
    {
        if (e.Data is XfsCashUnitData u)
        {
            data["unit"] = u.Number.ToString();
            data["unitId"] = u.UnitId;
            data["status"] = XfsValues.ToCashUnitStatus(u.Status).ToString();
        }
        return ev;
    }
}

/// <summary>WFS_SERVICE_EVENT: device state changes unrelated to a command (safe door, media removed, SIU port status).</summary>
public sealed class ServiceEventHandler(XfsEventCatalog catalog, SiuSensorMap sensors) : CatalogEventHandler(catalog)
{
    public override XfsEventKind Kind => XfsEventKind.Service;

    protected override DeviceEvent Enrich(XfsEvent e, XfsEventDefinition def, DeviceEvent ev, Dictionary<string, string> data)
    {
        if (e.Data is not XfsSiuPortEventData p) return ev;

        var entry = sensors.Find(p.PortType, p.PortIndex);
        data["portType"] = p.PortType.ToString();
        data["portIndex"] = p.PortIndex.ToString();
        data["portStatus"] = p.PortStatus.ToString();
        if (entry is null) return ev with { Message = $"SIU port {p.PortType}/{p.PortIndex} status {p.PortStatus}" };

        bool active = (p.PortStatus & entry.ActiveMask) != 0;
        data["sensor"] = entry.Kind.ToString();
        data["active"] = active.ToString();

        var (type, severity, msg) = (entry.Kind, active) switch
        {
            (SensorKind.SafeDoor, true) => (DeviceEventType.SafeDoorOpen, AlertSeverity.Critical, "Safe door opened"),
            (SensorKind.SafeDoor, false) => (DeviceEventType.SafeDoorClosed, AlertSeverity.Info, "Safe door closed"),
            (SensorKind.CabinetDoor or SensorKind.VandalShield, true) => (DeviceEventType.CabinetDoorOpen, AlertSeverity.Major, $"{entry.Name} opened"),
            (SensorKind.CabinetDoor or SensorKind.VandalShield, false) => (DeviceEventType.CabinetDoorClosed, AlertSeverity.Info, $"{entry.Name} closed"),
            (SensorKind.Tamper or SensorKind.InternalTamper, true) => (DeviceEventType.TamperDetected, AlertSeverity.Critical, $"{entry.Name} triggered"),
            (SensorKind.Vibration, true) => (DeviceEventType.VibrationDetected, AlertSeverity.Major, "Vibration/seismic sensor triggered"),
            (SensorKind.Alarm, true) => (DeviceEventType.AlarmTriggered, AlertSeverity.Critical, "ATM alarm triggered"),
            (SensorKind.Temperature, true) => (DeviceEventType.TemperatureHigh, AlertSeverity.Major, "Heat sensor triggered"),
            _ => (DeviceEventType.DeviceStatusChanged, AlertSeverity.Info, $"{entry.Name} {(active ? "active" : "inactive")}")
        };
        return ev with { Type = type, Severity = severity, Message = msg };
    }
}

/// <summary>WFS_USER_EVENT: operator-actionable conditions (cash unit threshold, paper threshold, retain bin).</summary>
public sealed class UserEventHandler(XfsEventCatalog catalog) : CatalogEventHandler(catalog)
{
    public override XfsEventKind Kind => XfsEventKind.User;

    protected override DeviceEvent Enrich(XfsEvent e, XfsEventDefinition def, DeviceEvent ev, Dictionary<string, string> data)
    {
        switch (e.Data)
        {
            case XfsCashUnitData u:
            {
                var status = XfsValues.ToCashUnitStatus(u.Status);
                data["unit"] = u.Number.ToString();
                data["unitId"] = u.UnitId;
                data["currency"] = u.Currency;
                data["denomination"] = u.Value.ToString();
                data["count"] = u.Count.ToString();
                data["status"] = status.ToString();
                var (type, sev, msg) = status switch
                {
                    CashUnitStatus.Empty => (DeviceEventType.CashEmpty, AlertSeverity.Critical, $"Cash unit {u.Number} ({u.Currency} {u.Value}) is empty"),
                    CashUnitStatus.Low => (DeviceEventType.CashLow, AlertSeverity.Major, $"Cash unit {u.Number} ({u.Currency} {u.Value}) is low: {u.Count} notes"),
                    CashUnitStatus.Full or CashUnitStatus.High => (DeviceEventType.CashHigh, AlertSeverity.Warning, $"Cash unit {u.Number} is {status}"),
                    _ => (DeviceEventType.CashUnitChanged, AlertSeverity.Info, $"Cash unit {u.Number} status {status}")
                };
                return ev with { Type = type, Severity = sev, Message = msg };
            }
            case XfsUShortData v when e.Class == DeviceClass.Printer:
            {
                var level = XfsValues.ToSupply(v.Value);
                data["paper"] = level.ToString();
                return level switch
                {
                    SupplyLevel.Out => ev with { Type = DeviceEventType.PaperEmpty, Severity = AlertSeverity.Major, Message = "Receipt paper empty" },
                    SupplyLevel.Low => ev with { Type = DeviceEventType.PaperLow, Severity = AlertSeverity.Warning, Message = "Receipt paper low" },
                    _ => ev with { Type = DeviceEventType.DeviceStatusChanged, Severity = AlertSeverity.Info, Message = "Receipt paper replenished" }
                };
            }
            case XfsUShortData v when e.Class is DeviceClass.CardReader or DeviceClass.NfcReader:
            {
                var bin = XfsValues.ToRetainBin(v.Value);
                data["retainBin"] = bin.ToString();
                return bin is RetainBinLevel.Full or RetainBinLevel.High
                    ? ev with { Message = $"Card retain bin {bin}" }
                    : ev with { Type = DeviceEventType.DeviceStatusChanged, Severity = AlertSeverity.Info, Message = "Card retain bin emptied" };
            }
            default:
                return ev;
        }
    }
}

/// <summary>WFS_SYSTEM_EVENT: XFS-manager level events (hardware error, fraud attempt, device status, app disconnect).</summary>
public sealed class SystemEventHandler(XfsEventCatalog catalog) : CatalogEventHandler(catalog)
{
    public override XfsEventKind Kind => XfsEventKind.System;

    protected override DeviceEvent Enrich(XfsEvent e, XfsEventDefinition def, DeviceEvent ev, Dictionary<string, string> data)
    {
        if (e.Data is XfsUShortData s && def.Type == DeviceEventType.DeviceStatusChanged)
        {
            var state = XfsValues.ToDeviceState(s.Value);
            data["state"] = state.ToString();
            var sev = state is DeviceState.Online or DeviceState.Busy ? AlertSeverity.Info
                : state is DeviceState.HardwareError or DeviceState.FraudAttempt ? AlertSeverity.Critical
                : AlertSeverity.Major;
            return ev with { Severity = sev, Message = $"Device state changed to {state}" };
        }
        return ev;
    }
}

public sealed class XfsEventProcessor
{
    private readonly Dictionary<XfsEventKind, IXfsEventHandler> _handlers;

    public XfsEventProcessor(XfsEventCatalog? catalog = null, SiuSensorMap? sensors = null)
    {
        catalog ??= XfsEventCatalog.Default;
        sensors ??= SiuSensorMap.Default;
        IXfsEventHandler[] all =
        [
            new ExecuteEventHandler(catalog), new ServiceEventHandler(catalog, sensors),
            new UserEventHandler(catalog), new SystemEventHandler(catalog)
        ];
        _handlers = all.ToDictionary(h => h.Kind);
    }

    public DeviceEvent? Process(XfsEvent e) => _handlers[e.Kind].Handle(e);
}
