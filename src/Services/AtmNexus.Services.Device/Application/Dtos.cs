namespace AtmNexus.Services.Device.Application;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

public class AtmSummary
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string? Model { get; set; }
    public string? BranchCode { get; set; }
    public string? Branch { get; set; }
    public string? City { get; set; }
    public int HealthScore { get; set; }
    public string HealthLevel { get; set; } = "HEALTHY";
    public string Connectivity { get; set; } = "UNKNOWN";
    public DateTime? LastSeenAt { get; set; }
    public string CommissionStatus { get; set; } = "";
}

public sealed class AtmDetail : AtmSummary
{
    public string? SerialNumber { get; set; }
    public string? IpAddress { get; set; }
    public string? OsVersion { get; set; }
    public string? AgentVersion { get; set; }
    public string? XfsVersion { get; set; }
    public string? ApplicationName { get; set; }
    public string? ApplicationVersion { get; set; }
    public DateTime? InstallDate { get; set; }
    public string? TagsJson { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

public sealed class ComponentRow
{
    public string LogicalName { get; set; } = "";
    public string DeviceClass { get; set; } = "";
    public string State { get; set; } = "UNKNOWN";
    public string? Vendor { get; set; }
    public string? Model { get; set; }
    public string? Firmware { get; set; }
    public string AttributesJson { get; set; } = "{}";
    public DateTime LastStateChangeAt { get; set; }
}

public sealed class CashUnitRow
{
    public int UnitNumber { get; set; }
    public string UnitId { get; set; } = "";
    public string UnitType { get; set; } = "";
    public string? Currency { get; set; }
    public int Denomination { get; set; }
    public int NoteCount { get; set; }
    public int InitialCount { get; set; }
    public int Minimum { get; set; }
    public int Maximum { get; set; }
    public string Status { get; set; } = "";
}

public sealed class AlertRow
{
    public Guid AlertId { get; set; }
    public string Code { get; set; } = "";
    public int Severity { get; set; }
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public string State { get; set; } = "OPEN";
    public string DataJson { get; set; } = "{}";
    public DateTime FirstRaisedAt { get; set; }
    public int Occurrences { get; set; }
}

public sealed class StatusRow
{
    public Guid AtmId { get; set; }
    public string AtmCode { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string? Model { get; set; }
    public int HealthScore { get; set; }
    public string HealthLevel { get; set; } = "HEALTHY";
    public string Connectivity { get; set; } = "UNKNOWN";
    public bool InService { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string ComponentsJson { get; set; } = "{}";
    public string ReasonsJson { get; set; } = "[]";
}

public sealed record AtmStatusDto(
    Guid AtmId, string AtmCode, int HealthScore, string HealthLevel, string Connectivity, bool InService, DateTime? LastSeenAt,
    IReadOnlyDictionary<string, int> Components, IReadOnlyList<string> Reasons,
    IReadOnlyList<DeviceStatusDto> Devices, IReadOnlyList<CashUnitRow> CashUnits, int OpenAlerts);

public sealed record DeviceStatusDto(string LogicalName, string DeviceClass, string State, IReadOnlyDictionary<string, string> Attributes, DateTime LastStateChangeAt);

public sealed record DashboardDto(
    int TotalAtms, IReadOnlyDictionary<string, int> ByHealthLevel, int Offline,
    IReadOnlyDictionary<string, int> OpenAlertsBySeverity, int CashLowUnits, int CashEmptyUnits, int OpenIncidents,
    IReadOnlyList<AtmSummary> Worst, DateTime GeneratedAt);

public sealed record ListAtmsQuery(string? Status, string? Connectivity, string? Branch, string? Vendor, string? Search, int Page, int PageSize);

public sealed record RestartRequest(string Target, string? Name, string? Reason);

public sealed class NotFoundException(string message) : Exception(message);
public sealed class ForbiddenException(string message) : Exception(message);
public sealed class ValidationFailedException(string message) : Exception(message);
public sealed class ConflictException(string message) : Exception(message);
