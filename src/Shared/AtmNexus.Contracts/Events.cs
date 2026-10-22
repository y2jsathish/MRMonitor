namespace AtmNexus.Contracts;

public static class Topics
{
    public const string AtmStatusChanged = "atm.status.changed";
    public const string AtmOffline = "atm.offline";
    public const string AlertCreated = "atm.alert.created";
    public const string IncidentCreated = "atm.incident.created";
    public const string DeploymentStarted = "deployment.started";
    public const string DeploymentCompleted = "deployment.completed";
    public const string EjBatch = "atm.ej.batch";
}

/// <summary>Kafka value schema for every platform event. Key = AtmId (guid string) to preserve per-ATM ordering.</summary>
public sealed record EventEnvelope<T>(
    Guid EventId,
    string EventType,
    int SchemaVersion,
    DateTimeOffset OccurredAt,
    Guid AtmId,
    string? CorrelationId,
    T Payload);

public sealed record AtmStatusChanged(
    string AtmCode,
    HealthLevel PreviousLevel,
    HealthLevel CurrentLevel,
    int HealthScore,
    IReadOnlyList<string> Reasons);

public sealed record AtmOffline(string AtmCode, DateTimeOffset LastSeenAt, int MissedHeartbeats);

public sealed record AlertCreated(
    Guid AlertId,
    string AtmCode,
    string Code,
    AlertSeverity Severity,
    string Source,
    string Message,
    DateTimeOffset RaisedAt,
    IReadOnlyDictionary<string, string>? Data);

public sealed record IncidentCreated(
    Guid IncidentId,
    string Number,
    string AtmCode,
    string Title,
    string Priority,
    string Category,
    Guid? SourceAlertId,
    string? AssignedGroup,
    DateTimeOffset CreatedAt);

public sealed record DeploymentStarted(
    Guid DeploymentId,
    Guid PackageId,
    string PackageVersion,
    string AtmCode,
    string Strategy,
    string StartedBy,
    DateTimeOffset StartedAt);

public sealed record DeploymentCompleted(
    Guid DeploymentId,
    string AtmCode,
    string Result,
    int DurationSeconds,
    string? FailureReason,
    DateTimeOffset CompletedAt);
