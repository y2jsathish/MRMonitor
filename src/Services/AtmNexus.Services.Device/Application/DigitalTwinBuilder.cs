using System.Text.Json;

namespace AtmNexus.Services.Device.Application;

public sealed record TwinFault(string Code, string Severity, string Description, DateTime? Since);

public sealed record TwinComponent(
    string Slot, string LogicalName, string DeviceClass, string State, string Health,
    IReadOnlyDictionary<string, string> Attributes, IReadOnlyList<TwinFault> Faults, IReadOnlyList<CashUnitRow>? CashUnits);

public sealed record TwinDocument(
    Guid AtmId, string AtmCode, string Vendor, string? Model, int HealthScore, string HealthLevel, string Connectivity,
    DateTime? LastSeenAt, IReadOnlyDictionary<string, int> HealthComponents,
    IReadOnlyList<TwinComponent> Components, IReadOnlyList<TwinFault> Faults, DateTime GeneratedAt);

/// <summary>
/// Maps raw device rows + alerts into the ATM digital twin:
/// ATM -> slots (Card Reader, EPP, Deposit Module, Dispenser, Sensors, Camera, Printer) -> device state, health model, fault model.
/// Pure function - the same code powers the REST twin API and the AI feature extractor.
/// </summary>
public static class DigitalTwinBuilder
{
    private sealed record Rule(string Key, string Value, string Code, string Severity, string Description);

    private static readonly Rule[] AttributeRules =
    [
        new("media", "jammed", "CARD_JAM", "CRITICAL", "Card jammed in reader"),
        new("retainbin", "full", "RETAIN_BIN_FULL", "MAJOR", "Card retain bin full"),
        new("retainbin", "high", "RETAIN_BIN_HIGH", "WARNING", "Card retain bin nearly full"),
        new("paper", "jammed", "PAPER_JAM", "MAJOR", "Receipt paper jam"),
        new("paper", "out", "PAPER_EMPTY", "MAJOR", "Receipt paper empty"),
        new("paper", "low", "PAPER_LOW", "WARNING", "Receipt paper low"),
        new("safedoor", "open", "SAFE_DOOR_OPEN", "CRITICAL", "Safe door open"),
        new("cabinetdoor", "open", "CABINET_DOOR_OPEN", "MAJOR", "Cabinet door open"),
        new("tamper", "active", "TAMPER", "CRITICAL", "Tamper sensor active"),
        new("vibration", "active", "VIBRATION", "MAJOR", "Vibration sensor active"),
        new("heat", "active", "TEMPERATURE_HIGH", "MAJOR", "Heat sensor active"),
        new("encryptor", "tampered", "EPP_TAMPERED", "CRITICAL", "PIN pad reports tamper"),
        new("dispenser", "custop", "DISPENSER_CU_STOP", "CRITICAL", "Dispenser cash unit stopped"),
        new("dispenser", "custate", "DISPENSER_CU_STATE", "MAJOR", "Dispenser cash unit state issue"),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TwinDocument Build(StatusRow status, IReadOnlyList<ComponentRow> components,
        IReadOnlyList<CashUnitRow> cashUnits, IReadOnlyList<AlertRow> openAlerts, DateTime now)
    {
        var twinComponents = new List<TwinComponent>();
        foreach (var c in components.OrderBy(c => SlotOrder(c.DeviceClass)).ThenBy(c => c.LogicalName))
        {
            var attrs = ParseAttributes(c.AttributesJson);
            var faults = new List<TwinFault>();

            switch (c.State)
            {
                case "HARDWARE_ERROR": faults.Add(new("HARDWARE_ERROR", "CRITICAL", $"{c.LogicalName} reports a hardware error", c.LastStateChangeAt)); break;
                case "FRAUD_ATTEMPT": faults.Add(new("FRAUD_ATTEMPT", "CRITICAL", $"{c.LogicalName} reports a fraud attempt", c.LastStateChangeAt)); break;
                case "POWER_OFF" or "NO_DEVICE" or "OFFLINE": faults.Add(new("DEVICE_UNAVAILABLE", "MAJOR", $"{c.LogicalName} is {c.State.ToLowerInvariant().Replace('_', ' ')}", c.LastStateChangeAt)); break;
            }

            foreach (var rule in AttributeRules)
                if (attrs.TryGetValue(rule.Key, out var v) && v.Equals(rule.Value, StringComparison.OrdinalIgnoreCase))
                    faults.Add(new(rule.Code, rule.Severity, rule.Description, c.LastStateChangeAt));

            IReadOnlyList<CashUnitRow>? units = null;
            if (c.DeviceClass is "CASH_DISPENSER" or "CASH_RECYCLER")
            {
                units = cashUnits;
                foreach (var u in cashUnits.Where(u => u.UnitType is "Dispense" or "Recycle"))
                {
                    if (u.Status == "Empty" || u.NoteCount == 0)
                        faults.Add(new("CASH_EMPTY", "CRITICAL", $"Cash unit {u.UnitNumber} ({u.Currency} {u.Denomination}) empty", null));
                    else if (u.Status == "Low" || u.NoteCount <= u.Minimum)
                        faults.Add(new("CASH_LOW", "MAJOR", $"Cash unit {u.UnitNumber} ({u.Currency} {u.Denomination}) low: {u.NoteCount}", null));
                    else if (u.Status is "Inoperative" or "Missing" or "Manipulated")
                        faults.Add(new("CASH_UNIT_FAULT", "MAJOR", $"Cash unit {u.UnitNumber} {u.Status}", null));
                }
            }

            // open alerts that belong to this component (source = logical name) but are not yet reflected as a fault
            foreach (var a in openAlerts.Where(a => a.Source.Equals(c.LogicalName, StringComparison.OrdinalIgnoreCase) || DeviceOf(a) == c.LogicalName))
                if (faults.All(f => f.Code != a.Code))
                    faults.Add(new(a.Code, SeverityName(a.Severity), a.Message, a.FirstRaisedAt));

            twinComponents.Add(new TwinComponent(SlotOf(c.DeviceClass), c.LogicalName, c.DeviceClass, c.State, HealthOf(c.State, faults),
                attrs, faults, units));
        }

        var all = twinComponents.SelectMany(c => c.Faults)
            .GroupBy(f => f.Code).Select(g => g.First())
            .OrderByDescending(f => Rank(f.Severity)).ToList();

        return new TwinDocument(status.AtmId, status.AtmCode, status.Vendor, status.Model, status.HealthScore, status.HealthLevel,
            status.Connectivity, status.LastSeenAt, ParseInts(status.ComponentsJson), twinComponents, all, now);
    }

    internal static string HealthOf(string state, IReadOnlyList<TwinFault> faults)
    {
        if (state is "OFFLINE" or "POWER_OFF" or "NO_DEVICE") return "OFFLINE";
        if (state is "HARDWARE_ERROR" or "FRAUD_ATTEMPT" || faults.Any(f => f.Severity == "CRITICAL")) return "FAULTED";
        if (state is "USER_ERROR" or "UNKNOWN" || faults.Count > 0) return "DEGRADED";
        return "HEALTHY";
    }

    internal static string SlotOf(string deviceClass) => deviceClass switch
    {
        "CARD_READER" or "NFC_READER" => "Card Reader",
        "PIN_PAD" => "EPP",
        "DEPOSITORY" => "Deposit Module",
        "CASH_DISPENSER" or "CASH_RECYCLER" => "Dispenser",
        "SENSORS" => "Sensors",
        "CAMERA" => "Camera",
        "PRINTER" => "Printer",
        _ => "Other"
    };

    private static int SlotOrder(string deviceClass) => SlotOf(deviceClass) switch
    {
        "Card Reader" => 0, "EPP" => 1, "Deposit Module" => 2, "Dispenser" => 3, "Sensors" => 4, "Camera" => 5, "Printer" => 6, _ => 7
    };

    private static int Rank(string s) => s switch { "CRITICAL" => 3, "MAJOR" => 2, "WARNING" => 1, _ => 0 };
    private static string SeverityName(int s) => s switch { 3 => "CRITICAL", 2 => "MAJOR", 1 => "WARNING", _ => "INFO" };

    private static string? DeviceOf(AlertRow a)
    {
        try
        {
            using var d = JsonDocument.Parse(a.DataJson);
            return d.RootElement.TryGetProperty("device", out var v) ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static Dictionary<string, string> ParseAttributes(string json) =>
        (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Json) ?? new())
            .ToDictionary(k => k.Key, k => k.Value.ValueKind == JsonValueKind.String ? k.Value.GetString() ?? "" : k.Value.ToString(), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, int> ParseInts(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, int>>(json, Json) ?? new();
}
