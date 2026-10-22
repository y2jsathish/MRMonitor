namespace AtmNexus.Agent;

public sealed class AgentOptions
{
    public const string Section = "Agent";

    public string AtmCode { get; set; } = "";
    public string AgentVersion { get; set; } = "0.1.0";
    public string GatewayBaseUrl { get; set; } = "https://nexus-gw.example.bank";
    public string CachePath { get; set; } = @"C:\ProgramData\ATMNexus\agent-cache.db";

    public TimeSpan DevicePollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan TelemetryInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan ServicePollInterval { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan CommandLongPoll { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan InventoryInterval { get; set; } = TimeSpan.FromHours(24);

    public int OutboxMaxRows { get; set; } = 50_000;
    public int TelemetryBatchSize { get; set; } = 200;

    /// <summary>Windows services that must be running (ATM application, XFS manager, vendor agents ...).</summary>
    public List<MonitoredService> Services { get; set; } = new();
    /// <summary>Processes that must exist (for ATM apps that are not services, e.g. ProBase/C ATM client).</summary>
    public List<string> Processes { get; set; } = new();
}

public sealed class MonitoredService
{
    public string Name { get; set; } = "";
    public bool Critical { get; set; }
    /// <summary>AtmApp | Xfs | Other - decides which health input a stopped service degrades.</summary>
    public string Role { get; set; } = "Other";
    public bool AutoRestart { get; set; }
    public int MaxAutoRestartsPerHour { get; set; } = 3;
}

public sealed class AlarmOptions
{
    public TimeSpan DedupWindow { get; set; } = TimeSpan.FromMinutes(5);
    public int FlapThreshold { get; set; } = 6;
    public TimeSpan FlapWindow { get; set; } = TimeSpan.FromMinutes(10);
}
