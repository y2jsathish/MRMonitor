namespace AtmNexus.Devices.Xfs;

public enum XfsPayloadKind { None, UShort, CdmCashUnit, SiuPort }
public enum XfsEventScope { Any, Idc, Cdm, Ptr, Pin, Siu, Dep }

public sealed record XfsEventDefinition(
    XfsEventScope Scope,
    XfsEventKind Kind,
    uint Id,
    string Name,
    DeviceEventType Type,
    AlertSeverity Severity,
    XfsPayloadKind Payload = XfsPayloadKind.None);

/// <summary>
/// Data table mapping (service scope, event kind, event id) to a domain event. IDs below follow CEN/XFS 3.x
/// (class offset + n). They are DATA, not logic: vendor profiles can override entries via
/// <c>Xfs:EventOverrides</c> after validating against the SDK headers (XFSCDM.H, XFSIDC.H, XFSSIU.H ...).
/// </summary>
public sealed class XfsEventCatalog
{
    public static readonly XfsEventCatalog Default = new(DefaultDefinitions());

    private readonly Dictionary<(XfsEventScope, XfsEventKind, uint), XfsEventDefinition> _map = new();

    public XfsEventCatalog(IEnumerable<XfsEventDefinition> definitions, IEnumerable<XfsEventDefinition>? overrides = null)
    {
        foreach (var d in definitions) _map[(d.Scope, d.Kind, d.Id)] = d;
        if (overrides is not null)
            foreach (var d in overrides) _map[(d.Scope, d.Kind, d.Id)] = d;
    }

    public static XfsEventScope ScopeOf(DeviceClass c) => c switch
    {
        DeviceClass.CardReader or DeviceClass.NfcReader => XfsEventScope.Idc,
        DeviceClass.CashDispenser or DeviceClass.CashRecycler => XfsEventScope.Cdm,
        DeviceClass.Printer => XfsEventScope.Ptr,
        DeviceClass.PinPad => XfsEventScope.Pin,
        DeviceClass.Sensors => XfsEventScope.Siu,
        DeviceClass.Depository => XfsEventScope.Dep,
        _ => XfsEventScope.Any
    };

    public XfsEventDefinition? Find(DeviceClass cls, XfsEventKind kind, uint id)
    {
        if (_map.TryGetValue((ScopeOf(cls), kind, id), out var d)) return d;
        return _map.TryGetValue((XfsEventScope.Any, kind, id), out d) ? d : null;
    }

    public XfsPayloadKind PayloadKind(DeviceClass cls, XfsEventKind kind, uint id) =>
        Find(cls, kind, id)?.Payload ?? XfsPayloadKind.None;

    private static IEnumerable<XfsEventDefinition> DefaultDefinitions()
    {
        const AlertSeverity I = AlertSeverity.Info, W = AlertSeverity.Warning, M = AlertSeverity.Major, C = AlertSeverity.Critical;
        const XfsEventKind Sys = XfsEventKind.System, Svc = XfsEventKind.Service, Usr = XfsEventKind.User, Exe = XfsEventKind.Execute;

        // WFS_SYSTEM_EVENT (class independent)
        yield return new(XfsEventScope.Any, Sys, 1, "WFS_SYSE_UNDELIVERABLE_MSG", DeviceEventType.Unknown, W);
        yield return new(XfsEventScope.Any, Sys, 2, "WFS_SYSE_HARDWARE_ERROR", DeviceEventType.HardwareError, C);
        yield return new(XfsEventScope.Any, Sys, 3, "WFS_SYSE_VERSION_ERROR", DeviceEventType.SoftwareError, M);
        yield return new(XfsEventScope.Any, Sys, 4, "WFS_SYSE_DEVICE_STATUS", DeviceEventType.DeviceStatusChanged, W, XfsPayloadKind.UShort);
        yield return new(XfsEventScope.Any, Sys, 5, "WFS_SYSE_APP_DISCONNECT", DeviceEventType.SoftwareError, W);
        yield return new(XfsEventScope.Any, Sys, 6, "WFS_SYSE_SOFTWARE_ERROR", DeviceEventType.SoftwareError, M);
        yield return new(XfsEventScope.Any, Sys, 7, "WFS_SYSE_USER_ERROR", DeviceEventType.UserError, W);
        yield return new(XfsEventScope.Any, Sys, 8, "WFS_SYSE_LOCK_REQUESTED", DeviceEventType.DeviceLockRequested, I);
        yield return new(XfsEventScope.Any, Sys, 9, "WFS_SYSE_FRAUD_ATTEMPT", DeviceEventType.FraudAttempt, C);

        // CDM / CRM
        yield return new(XfsEventScope.Cdm, Svc, 201, "WFS_SRVE_CDM_SAFEDOOROPEN", DeviceEventType.SafeDoorOpen, C);
        yield return new(XfsEventScope.Cdm, Svc, 202, "WFS_SRVE_CDM_SAFEDOORCLOSED", DeviceEventType.SafeDoorClosed, I);
        yield return new(XfsEventScope.Cdm, Svc, 203, "WFS_SRVE_CDM_CASHUNITINFOCHANGED", DeviceEventType.CashUnitChanged, I, XfsPayloadKind.CdmCashUnit);
        yield return new(XfsEventScope.Cdm, Svc, 205, "WFS_SRVE_CDM_ITEMSTAKEN", DeviceEventType.CashTaken, I);
        yield return new(XfsEventScope.Cdm, Usr, 201, "WFS_USRE_CDM_CASHUNITTHRESHOLD", DeviceEventType.CashUnitChanged, W, XfsPayloadKind.CdmCashUnit);
        yield return new(XfsEventScope.Cdm, Exe, 203, "WFS_EXEE_CDM_CASHUNITERROR", DeviceEventType.HardwareError, M, XfsPayloadKind.CdmCashUnit);

        // IDC
        yield return new(XfsEventScope.Idc, Svc, 101, "WFS_SRVE_IDC_MEDIAREMOVED", DeviceEventType.CardTaken, I);
        yield return new(XfsEventScope.Idc, Svc, 102, "WFS_SRVE_IDC_CARDRETAINED", DeviceEventType.CardRetained, W);
        yield return new(XfsEventScope.Idc, Usr, 101, "WFS_USRE_IDC_RETAINBINTHRESHOLD", DeviceEventType.RetainBinHigh, W, XfsPayloadKind.UShort);
        yield return new(XfsEventScope.Idc, Exe, 101, "WFS_EXEE_IDC_INVALIDTRACKDATA", DeviceEventType.UserError, W);
        yield return new(XfsEventScope.Idc, Exe, 103, "WFS_EXEE_IDC_MEDIAINSERTED", DeviceEventType.CardInserted, I);

        // PTR
        yield return new(XfsEventScope.Ptr, Usr, 301, "WFS_USRE_PTR_PAPERTHRESHOLD", DeviceEventType.PaperLow, W, XfsPayloadKind.UShort);
        yield return new(XfsEventScope.Ptr, Exe, 301, "WFS_EXEE_PTR_NOMEDIA", DeviceEventType.PaperJam, M);

        // SIU
        yield return new(XfsEventScope.Siu, Svc, 801, "WFS_SRVE_SIU_PORT_STATUS", DeviceEventType.Unknown, I, XfsPayloadKind.SiuPort);
    }
}
