namespace AtmNexus.Devices.Xfs;

[Flags]
public enum XfsEventClass : uint { None = 0, Service = 1, User = 2, System = 4, Execute = 8, All = 15 }

/// <summary>WFS_SERVICE_EVENT, WFS_USER_EVENT, WFS_SYSTEM_EVENT, WFS_EXECUTE_EVENT.</summary>
public enum XfsEventKind { Service, User, System, Execute }

public sealed record XfsOpenInfo(ushort Handle, ushort ServiceVersion, ushort SpiVersion, string Description);

public sealed record XfsResponse(int HResult, object? Data)
{
    public bool Success => HResult == 0;
    public T? DataAs<T>() where T : class => Data as T;
}

/// <summary>Event as delivered by the XFS manager (handle only).</summary>
public sealed record XfsApiEvent(XfsEventKind Kind, ushort Handle, uint EventId, int HResult, object? Data, DateTimeOffset At);

/// <summary>Event enriched with the logical service name / device class by <see cref="XfsServiceManager"/>.</summary>
public sealed record XfsEvent(XfsEventKind Kind, string LogicalName, DeviceClass Class, uint EventId, int HResult, object? Data, DateTimeOffset At);

public sealed class XfsException : Exception
{
    public string Operation { get; }
    public string LogicalName { get; }
    public int XfsResult { get; }

    public XfsException(string operation, string logicalName, int xfsResult, string? detail = null)
        : base($"XFS {operation} on '{logicalName}' failed with hResult {xfsResult}{(detail is null ? "" : ": " + detail)}")
    {
        Operation = operation;
        LogicalName = logicalName;
        XfsResult = xfsResult;
    }
}

/// <summary>
/// Thin, synchronous seam over msxfs.dll (WFS* functions). Implementations: Win32XfsApi (production),
/// MockXfsApi (simulator/tests). Everything above this seam is portable and unit-testable.
/// </summary>
public interface IXfsApi : IDisposable
{
    event Action<XfsApiEvent>? EventReceived;
    void StartUp();
    XfsOpenInfo Open(string logicalName, DeviceClass deviceClass, TimeSpan timeout);
    int Close(ushort handle);
    int Register(ushort handle, XfsEventClass classes);
    int Deregister(ushort handle, XfsEventClass classes);
    int Lock(ushort handle, TimeSpan timeout);
    int Unlock(ushort handle);
    XfsResponse GetInfo(ushort handle, uint category, object? query, TimeSpan timeout);
    XfsResponse Execute(ushort handle, uint command, object? data, TimeSpan timeout);
}

public sealed class XfsApiOptions
{
    public string AppId { get; set; } = "ATMNexusAgent";
    /// <summary>XFS version encoding: low byte = major, high byte = minor. 3.00 = 0x0003, 3.10 = 0x0A03, 3.30 = 0x1E03.</summary>
    public ushort LowVersion { get; set; } = 0x0003;
    public ushort HighVersion { get; set; } = 0x0003;
    public uint TraceLevel { get; set; }
    public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(15);
}

// ---------- Managed payloads (what the marshalling layer produces / consumes) ----------
public sealed record XfsStatusData(
    IReadOnlyDictionary<string, ushort> Fields,
    IReadOnlyDictionary<string, ushort[]> Arrays,
    string? Extra)
{
    public ushort Field(string name, ushort fallback = 0xFFFF) => Fields.TryGetValue(name, out var v) ? v : fallback;
    public ushort[] Array(string name) => Arrays.TryGetValue(name, out var v) ? v : [];
}

public sealed record XfsCashUnitData(
    ushort Number, ushort Type, string Name, string UnitId, string Currency, uint Value,
    uint InitialCount, uint Count, uint RejectCount, uint Minimum, uint Maximum, bool AppLock, ushort Status);

public sealed record XfsCashUnitInfoData(IReadOnlyList<XfsCashUnitData> Units);
public sealed record XfsCdmDispenseData(ushort TellerId, ushort MixNumber, string Currency, uint Amount, bool Present);
public sealed record XfsDispenseResultData(string Currency, uint Amount, IReadOnlyList<(uint Denomination, uint Count)> Notes);
public sealed record XfsCardDataItem(ushort Source, ushort Status, byte[] Data);
public sealed record XfsCardDataResult(IReadOnlyList<XfsCardDataItem> Items);
public sealed record XfsSiuPortEventData(ushort PortType, ushort PortIndex, ushort PortStatus);
public sealed record XfsUShortData(ushort Value);

// ---------- Identifier tables (CEN/XFS 3.x). Verify against XFS*.H of the SDK shipped by the vendor. ----------
public static class XfsIds
{
    public const int Success = 0;

    public static class Idc
    {
        public const uint InfoStatus = 101, InfoCapabilities = 102;
        public const uint CmdReadTrack = 101, CmdEjectCard = 103, CmdRetainCard = 104, CmdReadRawData = 107, CmdReset = 110;
        public const ushort Track1 = 0x0001, Track2 = 0x0002, Track3 = 0x0004, Chip = 0x0008, Security = 0x0010;
    }

    public static class Cdm
    {
        public const uint InfoStatus = 201, InfoCapabilities = 202, InfoCashUnitInfo = 203;
        public const uint CmdDenominate = 201, CmdDispense = 202, CmdPresent = 203, CmdReject = 204, CmdRetract = 205, CmdReset = 215;
    }

    public static class Ptr
    {
        public const uint InfoStatus = 301, InfoCapabilities = 302;
        public const uint CmdReset = 313;
    }

    public static class Pin
    {
        public const uint InfoStatus = 401, InfoCapabilities = 402;
        public const uint CmdReset = 415;
    }

    public static class Siu
    {
        public const uint InfoStatus = 801, InfoCapabilities = 802;
        public const uint CmdEnableEvents = 801, CmdReset = 807;
    }
}

/// <summary>Leading USHORT members of each WFS*STATUS struct. Vendors extend structs - override per vendor profile.</summary>
public sealed record StatusField(string Name, int Count = 1);

public static class XfsStatusLayouts
{
    public static readonly IReadOnlyList<StatusField> Idc =
        [new("fwDevice"), new("fwMedia"), new("fwRetainBin"), new("fwSecurity"), new("usCards"), new("fwChipPower")];
    public static readonly IReadOnlyList<StatusField> Cdm =
        [new("fwDevice"), new("fwSafeDoor"), new("fwDispenser"), new("fwIntermediateStacker")];
    public static readonly IReadOnlyList<StatusField> Ptr =
        [new("fwDevice"), new("fwMedia"), new("fwPaper", 16), new("fwToner"), new("fwInk"), new("fwLamp")];
    public static readonly IReadOnlyList<StatusField> Pin =
        [new("fwDevice"), new("fwEncStat")];
    public static readonly IReadOnlyList<StatusField> Siu =
        [new("fwDevice"), new("fwSensors", 16), new("fwDoors", 16), new("fwIndicators", 16), new("fwAuxiliaries", 16), new("fwGuidLights", 16)];

    public static IReadOnlyList<StatusField>? ForCategory(uint category) => category switch
    {
        XfsIds.Idc.InfoStatus => Idc,
        XfsIds.Cdm.InfoStatus => Cdm,
        XfsIds.Ptr.InfoStatus => Ptr,
        XfsIds.Pin.InfoStatus => Pin,
        XfsIds.Siu.InfoStatus => Siu,
        _ => null
    };
}

/// <summary>Generic WFS_STAT_DEV* values -> normalised <see cref="DeviceState"/>.</summary>
public static class XfsValues
{
    public static DeviceState ToDeviceState(ushort fwDevice) => fwDevice switch
    {
        0 => DeviceState.Online,
        1 => DeviceState.Offline,
        2 => DeviceState.PowerOff,
        3 => DeviceState.NoDevice,
        4 => DeviceState.HardwareError,
        5 => DeviceState.UserError,
        6 => DeviceState.Busy,
        7 => DeviceState.FraudAttempt,
        _ => DeviceState.Unknown
    };

    public static CashUnitStatus ToCashUnitStatus(ushort s) => s switch
    {
        0 => CashUnitStatus.Ok, 1 => CashUnitStatus.Full, 2 => CashUnitStatus.High, 3 => CashUnitStatus.Low,
        4 => CashUnitStatus.Empty, 5 => CashUnitStatus.Inoperative, 6 => CashUnitStatus.Missing,
        7 => CashUnitStatus.NoValue, 8 => CashUnitStatus.NoReference, 9 => CashUnitStatus.Manipulated,
        _ => CashUnitStatus.Unknown
    };

    public static CashUnitType ToCashUnitType(ushort t) => t switch
    {
        2 => CashUnitType.Reject, 3 => CashUnitType.Dispense, 4 => CashUnitType.Coin, 5 => CashUnitType.Coin,
        6 => CashUnitType.Retract, 12 => CashUnitType.Recycle, 11 => CashUnitType.Deposit, _ => CashUnitType.Unknown
    };

    public static CardMedia ToCardMedia(ushort v) => v switch
    {
        0 => CardMedia.Present, 1 => CardMedia.NotPresent, 2 => CardMedia.Jammed, 3 => CardMedia.NotSupported,
        4 => CardMedia.Unknown, 5 => CardMedia.Entering, 6 => CardMedia.Latched, _ => CardMedia.Unknown
    };

    public static RetainBinLevel ToRetainBin(ushort v) => v switch
    {
        0 => RetainBinLevel.Ok, 1 => RetainBinLevel.Full, 2 => RetainBinLevel.High,
        3 => RetainBinLevel.NotSupported, _ => RetainBinLevel.Unknown
    };

    public static SupplyLevel ToSupply(ushort v) => v switch
    {
        0 => SupplyLevel.Full, 1 => SupplyLevel.Low, 2 => SupplyLevel.Out, 3 => SupplyLevel.NotSupported, _ => SupplyLevel.Unknown
    };

    public static PrinterMedia ToPrinterMedia(ushort v) => v switch
    {
        0 => PrinterMedia.Present, 1 => PrinterMedia.NotPresent, 2 => PrinterMedia.Jammed,
        3 => PrinterMedia.NotSupported, 5 => PrinterMedia.Entering, _ => PrinterMedia.Unknown
    };

    public static EncryptorState ToEncryptor(ushort v) => v switch
    {
        0 => EncryptorState.Ready, 1 => EncryptorState.NotReady, 2 => EncryptorState.NotInitialized,
        3 => EncryptorState.Busy, 4 => EncryptorState.Undefined, 5 => EncryptorState.Initialized,
        6 => EncryptorState.Tampered, _ => EncryptorState.Undefined
    };
}
