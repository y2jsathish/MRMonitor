using System.Security.Cryptography;

namespace AtmNexus.Devices;

/// <summary>Configuration for one logical device. Bound from appsettings (Devices:[n]).</summary>
public sealed class DeviceDescriptor
{
    public string LogicalName { get; set; } = "";
    public DeviceClass Class { get; set; }
    /// <summary>NCR | DieboldNixdorf | Wincor | Hyosung | GRG | Hitachi | KAL | Generic</summary>
    public string Vendor { get; set; } = "Generic";
    /// <summary>xfs3 | xfs4iot | simulator</summary>
    public string Adapter { get; set; } = "xfs3";
    /// <summary>XFS4IoT: wss://localhost:5846/xfs4iot/v1.0/... ; XFS3: unused.</summary>
    public string? Endpoint { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new();

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var v) ? v : fallback;
}

public sealed record DeviceStatus(
    string LogicalName,
    DeviceClass Class,
    DeviceState State,
    IReadOnlyDictionary<string, string> Attributes,
    DateTimeOffset At)
{
    public bool IsOperational => State is DeviceState.Online or DeviceState.Busy;
}

public enum DeviceEventType
{
    CashLow, CashEmpty, CashHigh, CashUnitChanged, CashTaken,
    CardInserted, CardTaken, CardRetained, CardJam, RetainBinHigh,
    PaperLow, PaperEmpty, PaperJam,
    SafeDoorOpen, SafeDoorClosed, CabinetDoorOpen, CabinetDoorClosed,
    TamperDetected, VibrationDetected, AlarmTriggered, TemperatureHigh,
    DeviceStatusChanged, HardwareError, SoftwareError, UserError, FraudAttempt, DeviceLockRequested,
    Unknown
}

public sealed record DeviceEvent(
    Guid Id,
    string LogicalName,
    DeviceClass Class,
    DeviceEventType Type,
    AlertSeverity Severity,
    string Message,
    DateTimeOffset At,
    IReadOnlyDictionary<string, string> Data)
{
    public static DeviceEvent Create(string logicalName, DeviceClass cls, DeviceEventType type,
        AlertSeverity severity, string message, DateTimeOffset? at = null,
        IReadOnlyDictionary<string, string>? data = null) =>
        new(Guid.NewGuid(), logicalName, cls, type, severity, message, at ?? DateTimeOffset.UtcNow,
            data ?? new Dictionary<string, string>());
}

// ---------- Cash ----------
public enum CashUnitType { Unknown, Reject, Dispense, Retract, Recycle, Deposit, Coin }

public enum CashUnitStatus
{
    Ok, Full, High, Low, Empty, Inoperative, Missing, NoValue, NoReference, Manipulated, Unknown
}

public sealed record CashUnit(
    int Number,
    string UnitId,
    string Name,
    CashUnitType Type,
    string Currency,
    int Denomination,
    int InitialCount,
    int Count,
    int RejectCount,
    int Minimum,
    int Maximum,
    CashUnitStatus Status,
    bool AppLocked);

public sealed record DispenseRequest(string Currency, long Amount, int MixNumber = 0, bool Present = true, int TellerId = 0);
public sealed record DispensedNote(int Denomination, int Count);
public sealed record DispenseResult(bool Success, long DispensedAmount, IReadOnlyList<DispensedNote> Notes, string? Error);

public sealed record CurrencyCash(string Currency, long Total, int Notes, IReadOnlyDictionary<int, int> NotesByDenomination);
public sealed record CashSummary(
    IReadOnlyDictionary<string, CurrencyCash> ByCurrency, int LowUnits, int EmptyUnits, int FaultyUnits);

public sealed record CashInResult(bool Success, long Amount, IReadOnlyList<DispensedNote> Notes, string? Error);

// ---------- Card reader ----------
public enum CardMedia { Present, NotPresent, Jammed, NotSupported, Unknown, Entering, Latched }
public enum RetainBinLevel { Ok, Full, High, NotSupported, Unknown }
public enum TrackDataStatus { Ok, Missing, Invalid, TooLong, TooShort, NotSupported, SourceMissing }

public sealed record CardReaderStatus(DeviceState State, CardMedia Media, RetainBinLevel RetainBin, int RetainedCards);
public sealed record CardReadOptions(bool Track1, bool Track2, bool Track3, bool Chip, TimeSpan Timeout, bool ReturnRawTracks = false);
public sealed record TrackReadStatus(int Track, TrackDataStatus Status, int Length);

/// <summary>
/// Result of a card read. PAN is only ever exposed masked (BIN6 + last4). Raw track bytes are returned only
/// when explicitly requested in diagnostic mode with an allow-listed test BIN; they are zeroed on dispose.
/// </summary>
public sealed record CardReadResult(
    bool CardPresent,
    bool ChipPresent,
    bool IsEmv,
    bool TechnicalFallbackSuspected,
    string? ServiceCode,
    string? MaskedPan,
    IReadOnlyList<TrackReadStatus> Tracks,
    IReadOnlyDictionary<int, SensitiveBytes>? RawTracks = null);

// ---------- Printer / PIN pad / sensors ----------
public enum SupplyLevel { Full, Low, Out, NotSupported, Unknown }
public enum PrinterMedia { Present, NotPresent, Jammed, NotSupported, Unknown, Entering }
public sealed record PrinterStatus(DeviceState State, PrinterMedia Media, SupplyLevel Paper, SupplyLevel Toner, bool PaperJammed);

public enum EncryptorState { Ready, NotReady, NotInitialized, Busy, Undefined, Initialized, Tampered }
public sealed record EppInfo(DeviceState State, EncryptorState Encryptor, string? Serial, string? Firmware, bool TamperSuspected);

public enum SensorKind { CabinetDoor, SafeDoor, VandalShield, Tamper, InternalTamper, Vibration, Alarm, Temperature, Proximity, OperatorSwitch }
public enum SensorState { Inactive, Active, Unknown }
public sealed record SensorReading(SensorKind Kind, string Name, SensorState State, double? Value);

/// <summary>Byte buffer that never prints its content and is wiped when disposed (PCI DSS 3.x / PA-DSS).</summary>
public sealed class SensitiveBytes : IDisposable
{
    private readonly byte[] _data;
    public SensitiveBytes(byte[] data) => _data = data;
    public int Length => _data.Length;
    public ReadOnlySpan<byte> Span => _data;
    public void Dispose() => CryptographicOperations.ZeroMemory(_data);
    public override string ToString() => $"[REDACTED {_data.Length} bytes]";
}
