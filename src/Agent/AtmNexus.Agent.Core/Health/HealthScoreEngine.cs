namespace AtmNexus.Agent.Health;

public sealed record ApplicationStatus(
    bool AtmAppRunning, bool XfsManagerRunning, TimeSpan? SinceLastTransaction, double ErrorsPerHour, bool InService)
{
    public static ApplicationStatus Unknown { get; } = new(true, true, null, 0, true);
}

public sealed record NetworkStatus(bool GatewayReachable, double LatencyMs, double PacketLossPct)
{
    public static NetworkStatus Unknown { get; } = new(true, 0, 0);
}

public sealed record SecurityStatus(bool TamperActive, bool SafeDoorOpen, bool CabinetOpen, bool FraudAttemptRecent, bool PatchCompliant)
{
    public static SecurityStatus Clean { get; } = new(false, false, false, false, true);
}

public sealed record AlarmRecord(DeviceEventType Type, AlertSeverity Severity, DateTimeOffset At);

public sealed record HealthInputs(
    DateTimeOffset Now,
    IReadOnlyList<DeviceStatus> Devices,
    IReadOnlyList<CashUnit> CashUnits,
    ApplicationStatus App,
    NetworkStatus Network,
    SecurityStatus Security,
    IReadOnlyList<AlarmRecord> History);

public sealed record HealthScoreResult(
    int Score, HealthLevel Level, IReadOnlyDictionary<string, int> Components, IReadOnlyList<string> Reasons);

public sealed class HealthScoreOptions
{
    // Component weights (sum = 100)
    public double DeviceWeight { get; set; } = 30;
    public double CashWeight { get; set; } = 20;
    public double ApplicationWeight { get; set; } = 15;
    public double NetworkWeight { get; set; } = 10;
    public double SecurityWeight { get; set; } = 15;
    public double HistoryWeight { get; set; } = 10;

    // Level thresholds (score >= x)
    public int HealthyAt { get; set; } = 85;
    public int DegradedAt { get; set; } = 60;
    public int CriticalAt { get; set; } = 30;

    // Hard caps
    public int TamperCap { get; set; } = 40;
    public int CannotDispenseCap { get; set; } = 50;
    /// <summary>Card reader or PIN pad unusable: the ATM cannot serve card transactions.</summary>
    public int CannotServeCardsCap { get; set; } = 50;
    public int ApplicationDownCap { get; set; } = 30;

    public TimeSpan HistoryHalfLife { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan HistoryLookback { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan TransactionStaleAfter { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Relative importance of each device class inside the device component.</summary>
    public Dictionary<DeviceClass, double> DeviceClassWeights { get; set; } = new()
    {
        [DeviceClass.CashDispenser] = 3, [DeviceClass.CashRecycler] = 3, [DeviceClass.CardReader] = 3, [DeviceClass.PinPad] = 3,
        [DeviceClass.Depository] = 2, [DeviceClass.Printer] = 1, [DeviceClass.Sensors] = 1, [DeviceClass.Camera] = 0.5
    };
}

/// <summary>
/// Deterministic 0-100 score: weighted components, then hard caps that encode "this ATM cannot serve customers / is under attack".
/// Pure function of <see cref="HealthInputs"/> so it runs identically on the ATM (offline) and in the platform (re-scoring).
/// </summary>
public sealed class HealthScoreEngine(HealthScoreOptions? options = null)
{
    private readonly HealthScoreOptions _o = options ?? new HealthScoreOptions();

    public HealthScoreResult Calculate(HealthInputs i)
    {
        var reasons = new List<string>();
        int device = ScoreDevices(i, reasons);
        int cash = ScoreCash(i, reasons, out bool cannotDispense);
        int app = ScoreApplication(i.App, reasons);
        int net = ScoreNetwork(i.Network, reasons);
        int sec = ScoreSecurity(i.Security, reasons);
        int hist = ScoreHistory(i, reasons);

        double totalW = _o.DeviceWeight + _o.CashWeight + _o.ApplicationWeight + _o.NetworkWeight + _o.SecurityWeight + _o.HistoryWeight;
        double raw = (device * _o.DeviceWeight + cash * _o.CashWeight + app * _o.ApplicationWeight
                      + net * _o.NetworkWeight + sec * _o.SecurityWeight + hist * _o.HistoryWeight) / totalW;
        int score = (int)Math.Round(raw);

        if (i.Security.TamperActive) { score = Math.Min(score, _o.TamperCap); reasons.Add($"Capped at {_o.TamperCap}: tamper active"); }
        bool dispenserDown = i.Devices.Any(d => d.Class is DeviceClass.CashDispenser or DeviceClass.CashRecycler
                                                && d.State is DeviceState.HardwareError or DeviceState.Offline or DeviceState.NoDevice or DeviceState.PowerOff);
        if (dispenserDown || cannotDispense)
        {
            score = Math.Min(score, _o.CannotDispenseCap);
            reasons.Add($"Capped at {_o.CannotDispenseCap}: ATM cannot dispense cash");
        }
        static bool Down(DeviceStatus d) => d.State is DeviceState.HardwareError or DeviceState.Offline or DeviceState.NoDevice
            or DeviceState.PowerOff or DeviceState.FraudAttempt;
        bool cardsDown = i.Devices.Any(d => d.Class is DeviceClass.CardReader or DeviceClass.NfcReader
            && (Down(d) || d.Attributes.GetValueOrDefault(DeviceAttributes.Media, "").Equals("jammed", StringComparison.OrdinalIgnoreCase)));
        bool pinDown = i.Devices.Any(d => d.Class == DeviceClass.PinPad
            && (Down(d) || d.Attributes.GetValueOrDefault(DeviceAttributes.Encryptor, "").Equals("tampered", StringComparison.OrdinalIgnoreCase)));
        if (cardsDown || pinDown)
        {
            score = Math.Min(score, _o.CannotServeCardsCap);
            reasons.Add($"Capped at {_o.CannotServeCardsCap}: ATM cannot accept cards or PIN entry");
        }
        if (!i.App.AtmAppRunning || !i.App.XfsManagerRunning)
        {
            score = Math.Min(score, _o.ApplicationDownCap);
            reasons.Add($"Capped at {_o.ApplicationDownCap}: ATM application or XFS manager not running");
        }

        score = Math.Clamp(score, 0, 100);
        var level = score >= _o.HealthyAt ? HealthLevel.Healthy
            : score >= _o.DegradedAt ? HealthLevel.Degraded
            : score >= _o.CriticalAt ? HealthLevel.Critical
            : HealthLevel.Down;

        return new HealthScoreResult(score, level, new Dictionary<string, int>
        {
            ["devices"] = device, ["cash"] = cash, ["application"] = app,
            ["network"] = net, ["security"] = sec, ["history"] = hist
        }, reasons);
    }

    // ---------------- components ----------------
    private int ScoreDevices(HealthInputs i, List<string> reasons)
    {
        if (i.Devices.Count == 0) return 100;
        double sum = 0, wsum = 0;
        foreach (var d in i.Devices)
        {
            double w = _o.DeviceClassWeights.GetValueOrDefault(d.Class, 1);
            int s = ScoreDevice(d);
            if (s < 80) reasons.Add($"{d.LogicalName} ({d.Class}) {d.State}{Detail(d)}");
            sum += s * w; wsum += w;
        }
        return (int)Math.Round(sum / wsum);
    }

    private static string Detail(DeviceStatus d)
    {
        var bad = d.Attributes.Where(a => a.Value is "Jammed" or "jammed" or "Out" or "Full" or "Tampered" or "cuStop" or "cuState" or "open" or "active")
            .Select(a => $"{a.Key}={a.Value}").ToList();
        return bad.Count == 0 ? "" : " [" + string.Join(", ", bad) + "]";
    }

    internal static int ScoreDevice(DeviceStatus d)
    {
        int s = d.State switch
        {
            DeviceState.Online or DeviceState.Busy => 100,
            DeviceState.UserError => 60,
            DeviceState.Unknown => 50,
            DeviceState.Offline => 30,
            _ => 0   // HardwareError, PowerOff, NoDevice, FraudAttempt
        };
        string A(string k) => d.Attributes.GetValueOrDefault(k, "").ToLowerInvariant();
        if (A(DeviceAttributes.Media) == "jammed") s = Math.Min(s, 20);
        if (A(DeviceAttributes.Paper) == "jammed") s = Math.Min(s, 20);
        if (A(DeviceAttributes.Paper) == "out") s = Math.Min(s, 40);
        if (A(DeviceAttributes.Paper) == "low") s = Math.Min(s, 80);
        if (A(DeviceAttributes.RetainBin) == "full") s = Math.Min(s, 40);
        if (A(DeviceAttributes.RetainBin) == "high") s = Math.Min(s, 80);
        if (A(DeviceAttributes.Encryptor) == "tampered") s = 0;
        if (A(DeviceAttributes.Dispenser) == "custop") s = Math.Min(s, 30);
        if (A(DeviceAttributes.Dispenser) == "custate") s = Math.Min(s, 70);
        return s;
    }

    private static int ScoreCash(HealthInputs i, List<string> reasons, out bool cannotDispense)
    {
        cannotDispense = false;
        var units = i.CashUnits.Where(u => u.Type is CashUnitType.Dispense or CashUnitType.Recycle).ToList();
        if (units.Count == 0) return 100;

        double sum = 0;
        foreach (var u in units)
        {
            int s;
            if (!CashCalculator.IsDispensable(u)) { s = 0; reasons.Add($"Cash unit {u.Number} ({u.Currency} {u.Denomination}) unusable: {u.Status}"); }
            else if (u.Status == CashUnitStatus.Low || u.Count <= u.Minimum) { s = 40; reasons.Add($"Cash unit {u.Number} ({u.Currency} {u.Denomination}) low: {u.Count}"); }
            else s = 100;
            sum += s;
        }
        if (!units.Any(CashCalculator.IsDispensable)) cannotDispense = true;
        return (int)Math.Round(sum / units.Count);
    }

    private int ScoreApplication(ApplicationStatus a, List<string> reasons)
    {
        if (!a.AtmAppRunning) { reasons.Add("ATM application not running"); return 0; }
        if (!a.XfsManagerRunning) { reasons.Add("XFS manager not running"); return 20; }
        int s = 100;
        if (!a.InService) { s -= 40; reasons.Add("ATM out of service"); }
        if (a.SinceLastTransaction is { } t && t > _o.TransactionStaleAfter) { s -= 30; reasons.Add($"No transaction for {t.TotalHours:F0}h"); }
        if (a.ErrorsPerHour > 0) s -= (int)Math.Min(40, a.ErrorsPerHour * 4);
        return Math.Max(0, s);
    }

    private static int ScoreNetwork(NetworkStatus n, List<string> reasons)
    {
        if (!n.GatewayReachable) { reasons.Add("Gateway unreachable"); return 0; }
        int s = 100;
        if (n.LatencyMs > 500) s -= 30;
        else if (n.LatencyMs > 200) s -= 10;
        if (n.PacketLossPct > 5) s -= 40;
        else if (n.PacketLossPct > 1) s -= 15;
        if (s < 80) reasons.Add($"Network degraded: {n.LatencyMs:F0}ms, {n.PacketLossPct:F1}% loss");
        return Math.Max(0, s);
    }

    private static int ScoreSecurity(SecurityStatus s, List<string> reasons)
    {
        int score = 100;
        if (s.TamperActive) { score = 0; reasons.Add("Tamper active"); }
        if (s.SafeDoorOpen) { score = Math.Min(score, 0); reasons.Add("Safe door open"); }
        if (s.CabinetOpen) { score = Math.Min(score, 30); reasons.Add("Cabinet open"); }
        if (s.FraudAttemptRecent) { score = Math.Min(score, 20); reasons.Add("Recent fraud attempt"); }
        if (!s.PatchCompliant) { score = Math.Max(0, score - 20); reasons.Add("OS patch level non-compliant"); }
        return score;
    }

    private int ScoreHistory(HealthInputs i, List<string> reasons)
    {
        double penalty = 0;
        foreach (var a in i.History)
        {
            var age = i.Now - a.At;
            if (age < TimeSpan.Zero || age > _o.HistoryLookback) continue;
            double w = a.Severity switch { AlertSeverity.Critical => 20, AlertSeverity.Major => 8, AlertSeverity.Warning => 3, _ => 0 };
            penalty += w * Math.Pow(0.5, age.TotalSeconds / _o.HistoryHalfLife.TotalSeconds);
        }
        int s = (int)Math.Max(0, Math.Round(100 - penalty));
        if (s < 70) reasons.Add($"Recent alarm history (score {s})");
        return s;
    }
}
