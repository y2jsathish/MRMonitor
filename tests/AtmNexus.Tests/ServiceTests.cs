using AtmNexus.Services.Device.Application;
using AtmNexus.Services.Device.Infrastructure;

namespace AtmNexus.Tests;

public class DigitalTwinBuilderTests
{
    private static readonly DateTime Now = new(2025, 6, 12, 10, 0, 0, DateTimeKind.Utc);

    private static StatusRow Status() => new()
    {
        AtmId = Guid.NewGuid(), AtmCode = "ATM-000124", Vendor = "DN", Model = "DN Series 200", HealthScore = 38, HealthLevel = "CRITICAL",
        Connectivity = "ONLINE", InService = true, LastSeenAt = Now, ComponentsJson = "{\"devices\":60,\"cash\":40}", ReasonsJson = "[]"
    };

    private static ComponentRow C(string name, string cls, string state, string attrs = "{}") =>
        new() { LogicalName = name, DeviceClass = cls, State = state, AttributesJson = attrs, LastStateChangeAt = Now.AddMinutes(-5) };

    private static CashUnitRow Cash(int n, int den, int count, string status, int min = 100) =>
        new() { UnitNumber = n, UnitId = $"C{n}", UnitType = "Dispense", Currency = "INR", Denomination = den, NoteCount = count, Minimum = min, Maximum = 2000, Status = status };

    [Fact]
    public void Twin_ModelsSlots_Health_AndFaults()
    {
        var twin = DigitalTwinBuilder.Build(Status(),
            [
                C("Printer1", "PRINTER", "ONLINE", "{\"paper\":\"Full\"}"),
                C("CDM", "CASH_DISPENSER", "ONLINE", "{\"safeDoor\":\"open\"}"),
                C("IDC30", "CARD_READER", "HARDWARE_ERROR", "{\"media\":\"Jammed\"}"),
                C("EPP", "PIN_PAD", "ONLINE")
            ],
            [Cash(1, 500, 1450, "Ok"), Cash(2, 2000, 90, "Low")],
            [new AlertRow { Code = "CARD_JAM", Severity = 3, Source = "IDC30", Message = "Card jammed", FirstRaisedAt = Now.AddMinutes(-4), DataJson = "{}" }],
            Now);

        // slot ordering follows the ATM hardware tree
        Assert.Equal(["Card Reader", "EPP", "Dispenser", "Printer"], twin.Components.Select(c => c.Slot).ToArray());

        var reader = twin.Components.Single(c => c.LogicalName == "IDC30");
        Assert.Equal("FAULTED", reader.Health);
        Assert.Equal(1, reader.Faults.Count(f => f.Code == "CARD_JAM"));        // alert + attribute rule collapse into one fault
        Assert.Contains(reader.Faults, f => f.Code == "HARDWARE_ERROR");

        var cdm = twin.Components.Single(c => c.LogicalName == "CDM");
        Assert.Equal("FAULTED", cdm.Health);                                    // open safe door is critical
        Assert.Contains(cdm.Faults, f => f.Code == "SAFE_DOOR_OPEN" && f.Severity == "CRITICAL");
        Assert.Contains(cdm.Faults, f => f.Code == "CASH_LOW" && f.Severity == "MAJOR");
        Assert.Equal(2, cdm.CashUnits!.Count);

        Assert.Equal("HEALTHY", twin.Components.Single(c => c.LogicalName == "Printer1").Health);
        Assert.Equal("HEALTHY", twin.Components.Single(c => c.LogicalName == "EPP").Health);

        // aggregated fault list is unique by code and sorted most severe first
        Assert.Equal(twin.Faults.Count, twin.Faults.Select(f => f.Code).Distinct().Count());
        Assert.Equal("CRITICAL", twin.Faults[0].Severity);
        Assert.Equal(60, twin.HealthComponents["devices"]);
    }

    [Theory]
    [InlineData("OFFLINE", "OFFLINE")]
    [InlineData("POWER_OFF", "OFFLINE")]
    [InlineData("USER_ERROR", "DEGRADED")]
    [InlineData("HARDWARE_ERROR", "FAULTED")]
    [InlineData("FRAUD_ATTEMPT", "FAULTED")]
    [InlineData("ONLINE", "HEALTHY")]
    [InlineData("BUSY", "HEALTHY")]
    public void ComponentHealth_FromState(string state, string expected) =>
        Assert.Equal(expected, DigitalTwinBuilder.HealthOf(state, []));

    [Fact]
    public void EmptyCassette_IsCriticalFault_OnTheDispenser()
    {
        var twin = DigitalTwinBuilder.Build(Status(), [C("CDM", "CASH_DISPENSER", "ONLINE")], [Cash(1, 500, 0, "Empty")], [], Now);
        Assert.Contains(twin.Components[0].Faults, f => f.Code == "CASH_EMPTY" && f.Severity == "CRITICAL");
        Assert.Equal("FAULTED", twin.Components[0].Health);
    }
}

public class DbNamesTests
{
    [Theory]
    [InlineData("CashDispenser", "CASH_DISPENSER")]
    [InlineData("HardwareError", "HARDWARE_ERROR")]
    [InlineData("Online", "ONLINE")]
    [InlineData("PinPad", "PIN_PAD")]
    [InlineData("Healthy", "HEALTHY")]
    public void Snake_MatchesSchemaCheckConstraints(string input, string expected) =>
        Assert.Equal(expected, DbNames.Snake(input));

    [Fact]
    public void EveryContractEnum_MapsToAValueAllowedByTheSchema()
    {
        // Mirrors ck_component_class / ck_component_state / ck_status_level in V001__core_schema.sql
        var classes = new HashSet<string> { "CARD_READER", "CASH_DISPENSER", "CASH_RECYCLER", "DEPOSITORY", "PRINTER", "PIN_PAD", "SENSORS", "CAMERA",
            "BARCODE_READER", "PASSPORT_READER", "NFC_READER", "BIOMETRIC" };
        var states = new HashSet<string> { "ONLINE", "OFFLINE", "POWER_OFF", "NO_DEVICE", "HARDWARE_ERROR", "USER_ERROR", "BUSY", "FRAUD_ATTEMPT", "UNKNOWN" };
        var levels = new HashSet<string> { "HEALTHY", "DEGRADED", "CRITICAL", "DOWN" };

        Assert.All(Enum.GetNames<DeviceClass>(), n => Assert.Contains(DbNames.Snake(n), classes));
        Assert.All(Enum.GetNames<DeviceState>(), n => Assert.Contains(DbNames.Snake(n), states));
        Assert.All(Enum.GetNames<HealthLevel>(), n => Assert.Contains(DbNames.Snake(n), levels));
    }

    [Fact]
    public void SqlMigration_ContainsEveryCheckValueUsedByTheCode()
    {
        var dir = AppContext.BaseDirectory;
        string? path = null;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "db", "migrations", "V001__core_schema.sql");
            if (File.Exists(candidate)) { path = candidate; break; }
        }
        Assert.NotNull(path);
        var sql = File.ReadAllText(path!);
        foreach (var n in Enum.GetNames<DeviceClass>()) Assert.Contains($"'{DbNames.Snake(n)}'", sql);
        foreach (var n in Enum.GetNames<DeviceState>()) Assert.Contains($"'{DbNames.Snake(n)}'", sql);
        foreach (var n in Enum.GetNames<RemoteCommandType>()) Assert.Contains($"'{n}'", sql);
    }
}

public class EventContractTests
{
    [Fact]
    public void Envelope_RoundTripsThroughContractJson()
    {
        var env = new EventEnvelope<AlertCreated>(Guid.NewGuid(), "atm.alert.created", 1, DateTimeOffset.UtcNow, Guid.NewGuid(), "corr-1",
            new AlertCreated(Guid.NewGuid(), "ATM-1", "CARD_JAM", AlertSeverity.Critical, "IDC", "jam", DateTimeOffset.UtcNow, new Dictionary<string, string> { ["device"] = "IDC" }));
        var json = System.Text.Json.JsonSerializer.Serialize(env, ContractJson.Options);
        Assert.Contains("\"severity\":\"Critical\"", json);       // enums as strings: stable across languages
        Assert.Contains("\"eventType\":\"atm.alert.created\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<EventEnvelope<AlertCreated>>(json, ContractJson.Options)!;
        Assert.Equal(env.Payload.Code, back.Payload.Code);
        Assert.Equal(AlertSeverity.Critical, back.Payload.Severity);
    }

    [Fact]
    public void Topics_MatchTheSpecification()
    {
        Assert.Equal(["atm.status.changed", "atm.offline", "atm.alert.created", "atm.incident.created", "deployment.started", "deployment.completed"],
            new[] { Topics.AtmStatusChanged, Topics.AtmOffline, Topics.AlertCreated, Topics.IncidentCreated, Topics.DeploymentStarted, Topics.DeploymentCompleted });
    }
}
