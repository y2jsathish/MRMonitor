namespace AtmNexus.Contracts;

public sealed record DeviceSnapshot(
    string LogicalName,
    DeviceClass Class,
    DeviceState State,
    string? Vendor,
    string? Model,
    string? Firmware,
    IReadOnlyDictionary<string, string> Attributes,
    DateTimeOffset At);

public sealed record CashUnitSnapshot(
    int Number,
    string UnitId,
    string Type,
    string Currency,
    int Denomination,
    int Count,
    int InitialCount,
    int RejectCount,
    int Minimum,
    int Maximum,
    string Status);

/// <summary>An event raised on the ATM (alarm, state change, application error).</summary>
public sealed record AgentEvent(
    Guid EventId,
    string Code,
    AlertSeverity Severity,
    string Source,
    string Message,
    DateTimeOffset At,
    IReadOnlyDictionary<string, string>? Data = null);

/// <summary>Payload of POST /api/agent/telemetry. Never contains PAN, track data, PIN or key material.</summary>
public sealed record AtmTelemetry(
    string AtmCode,
    string AgentVersion,
    DateTimeOffset At,
    int HealthScore,
    HealthLevel Level,
    IReadOnlyList<DeviceSnapshot> Devices,
    IReadOnlyList<CashUnitSnapshot> CashUnits,
    IReadOnlyList<AgentEvent> Events,
    IReadOnlyDictionary<string, double> Metrics);
