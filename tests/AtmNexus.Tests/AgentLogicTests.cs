using AtmNexus.Agent.Devices;
using AtmNexus.Agent.Health;

namespace AtmNexus.Tests;

public class HealthScoreEngineTests
{
    private static readonly DateTimeOffset Now = new(2025, 6, 12, 10, 0, 0, TimeSpan.Zero);
    private readonly HealthScoreEngine _engine = new();

    private static DeviceStatus Dev(string name, DeviceClass c, DeviceState s = DeviceState.Online, params (string, string)[] attrs) =>
        new(name, c, s, attrs.ToDictionary(a => a.Item1, a => a.Item2), Now);

    private static HealthInputs Inputs(
        IReadOnlyList<DeviceStatus>? devices = null, IReadOnlyList<CashUnit>? cash = null, ApplicationStatus? app = null,
        NetworkStatus? net = null, SecurityStatus? sec = null, IReadOnlyList<AlarmRecord>? history = null) =>
        new(Now,
            devices ?? [Dev("CDM", DeviceClass.CashDispenser), Dev("IDC", DeviceClass.CardReader), Dev("PIN", DeviceClass.PinPad),
                        Dev("PTR", DeviceClass.Printer), Dev("SIU", DeviceClass.Sensors)],
            cash ?? [CashCalculatorTests.U(1, 20, 1000), CashCalculatorTests.U(2, 50, 800)],
            app ?? ApplicationStatus.Unknown, net ?? NetworkStatus.Unknown, sec ?? SecurityStatus.Clean, history ?? []);

    [Fact]
    public void AllGreen_Scores100_Healthy()
    {
        var r = _engine.Calculate(Inputs());
        Assert.Equal(100, r.Score);
        Assert.Equal(HealthLevel.Healthy, r.Level);
        Assert.Empty(r.Reasons);
    }

    [Fact]
    public void PaperOut_IsMinorDegradation_StillHealthy()
    {
        var r = _engine.Calculate(Inputs(devices:
            [Dev("CDM", DeviceClass.CashDispenser), Dev("IDC", DeviceClass.CardReader), Dev("PIN", DeviceClass.PinPad),
             Dev("PTR", DeviceClass.Printer, DeviceState.Online, (DeviceAttributes.Paper, "Out")), Dev("SIU", DeviceClass.Sensors)]));
        Assert.InRange(r.Score, 90, 99);
        Assert.Equal(HealthLevel.Healthy, r.Level);
        Assert.Contains(r.Reasons, x => x.Contains("PTR"));
    }

    [Fact]
    public void Tamper_CapsScoreAt40()
    {
        var r = _engine.Calculate(Inputs(sec: SecurityStatus.Clean with { TamperActive = true }));
        Assert.True(r.Score <= 40);
        Assert.NotEqual(HealthLevel.Healthy, r.Level);
        Assert.Contains(r.Reasons, x => x.Contains("Tamper"));
    }

    [Fact]
    public void DispenserHardwareError_CapsAt50()
    {
        var r = _engine.Calculate(Inputs(devices:
            [Dev("CDM", DeviceClass.CashDispenser, DeviceState.HardwareError), Dev("IDC", DeviceClass.CardReader)]));
        Assert.True(r.Score <= 50);
        Assert.Contains(r.Reasons, x => x.Contains("cannot dispense"));
    }

    [Fact]
    public void JammedCardReader_CapsAt50_BecauseNoCardTransactionIsPossible()
    {
        var r = _engine.Calculate(Inputs(devices:
            [Dev("CDM", DeviceClass.CashDispenser), Dev("IDC", DeviceClass.CardReader, DeviceState.Online, (DeviceAttributes.Media, "Jammed")),
             Dev("PIN", DeviceClass.PinPad), Dev("PTR", DeviceClass.Printer), Dev("SIU", DeviceClass.Sensors)]));
        Assert.True(r.Score <= 50);
        Assert.Equal(HealthLevel.Critical, r.Level);
        Assert.Contains(r.Reasons, x => x.Contains("cannot accept cards"));
    }

    [Fact]
    public void TamperedEpp_CapsAt50()
    {
        var r = _engine.Calculate(Inputs(devices:
            [Dev("CDM", DeviceClass.CashDispenser), Dev("IDC", DeviceClass.CardReader),
             Dev("PIN", DeviceClass.PinPad, DeviceState.Online, (DeviceAttributes.Encryptor, "Tampered"))]));
        Assert.True(r.Score <= 50);
    }

    [Fact]
    public void AllCassettesEmpty_CannotDispense_CapsAt50_AndCashComponentIsZero()
    {
        var r = _engine.Calculate(Inputs(cash:
            [CashCalculatorTests.U(1, 20, 0, CashUnitStatus.Empty), CashCalculatorTests.U(2, 50, 0, CashUnitStatus.Empty)]));
        Assert.Equal(0, r.Components["cash"]);
        Assert.True(r.Score <= 50);
    }

    [Fact]
    public void OneLowCassette_DegradesCash_ButAtmCanStillDispense()
    {
        var r = _engine.Calculate(Inputs(cash:
            [CashCalculatorTests.U(1, 20, 1000), CashCalculatorTests.U(2, 50, 90, CashUnitStatus.Low, min: 100)]));
        Assert.Equal(70, r.Components["cash"]);          // (100 + 40) / 2
        Assert.Equal(HealthLevel.Healthy, r.Level);      // 100 - 20% * 30 = 94
    }

    [Fact]
    public void ApplicationDown_CapsAt30_Critical()
    {
        var r = _engine.Calculate(Inputs(app: ApplicationStatus.Unknown with { AtmAppRunning = false }));
        Assert.Equal(30, r.Score);
        Assert.Equal(HealthLevel.Critical, r.Level);
    }

    [Fact]
    public void GatewayUnreachable_OnlyCostsNetworkWeight()
    {
        var r = _engine.Calculate(Inputs(net: new NetworkStatus(false, 0, 100)));
        Assert.Equal(0, r.Components["network"]);
        Assert.Equal(90, r.Score);
    }

    [Fact]
    public void AlarmHistory_DecaysWithHalfLife()
    {
        var fresh = _engine.Calculate(Inputs(history: [new AlarmRecord(DeviceEventType.HardwareError, AlertSeverity.Critical, Now)]));
        var old = _engine.Calculate(Inputs(history: [new AlarmRecord(DeviceEventType.HardwareError, AlertSeverity.Critical, Now.AddHours(-12))]));
        var expired = _engine.Calculate(Inputs(history: [new AlarmRecord(DeviceEventType.HardwareError, AlertSeverity.Critical, Now.AddHours(-30))]));
        Assert.Equal(80, fresh.Components["history"]);
        Assert.Equal(95, old.Components["history"]);       // two half-lives: 20 * 0.25 = 5
        Assert.Equal(100, expired.Components["history"]);
    }

    [Fact]
    public void CardJam_ScoresDeviceLow()
    {
        var jam = Dev("IDC", DeviceClass.CardReader, DeviceState.Online, (DeviceAttributes.Media, "Jammed"));
        Assert.Equal(20, HealthScoreEngine.ScoreDevice(jam));
    }
}

public class StatusReconcilerTests
{
    private readonly StatusReconciler _r = new();
    private static readonly DateTimeOffset T = DateTimeOffset.UtcNow;

    private static DeviceStatus S(DeviceClass c, DeviceState st, params (string, string)[] a) =>
        new("DEV", c, st, a.ToDictionary(x => x.Item1, x => x.Item2), T);

    [Fact]
    public void CardJam_RaisedOnce_OnTransition()
    {
        var ok = S(DeviceClass.CardReader, DeviceState.Online, (DeviceAttributes.Media, "NotPresent"));
        var jam = S(DeviceClass.CardReader, DeviceState.Online, (DeviceAttributes.Media, "Jammed"));
        var first = _r.Reconcile(ok, jam);
        Assert.Contains(first, e => e.Type == DeviceEventType.CardJam && e.Severity == AlertSeverity.Critical);
        Assert.Empty(_r.Reconcile(jam, jam));
    }

    [Fact]
    public void StateTransitions_ProduceHardwareErrorAndRecovery()
    {
        var online = S(DeviceClass.CashDispenser, DeviceState.Online);
        var hw = S(DeviceClass.CashDispenser, DeviceState.HardwareError);
        Assert.Single(_r.Reconcile(online, hw), e => e.Type == DeviceEventType.HardwareError);
        Assert.Contains(_r.Reconcile(hw, online), e => e.Type == DeviceEventType.DeviceStatusChanged && e.Severity == AlertSeverity.Info);
    }

    [Fact]
    public void FirstPoll_WithAbnormalState_RaisesBaselineAlarm()
    {
        var jam = S(DeviceClass.CardReader, DeviceState.Online, (DeviceAttributes.Media, "Jammed"));
        Assert.Contains(_r.Reconcile(null, jam), e => e.Type == DeviceEventType.CardJam);
    }

    [Theory]
    [InlineData("Low", DeviceEventType.PaperLow)]
    [InlineData("Out", DeviceEventType.PaperEmpty)]
    [InlineData("Jammed", DeviceEventType.PaperJam)]
    public void Paper_Transitions(string level, DeviceEventType expected)
    {
        var full = S(DeviceClass.Printer, DeviceState.Online, (DeviceAttributes.Paper, "Full"));
        var cur = S(DeviceClass.Printer, DeviceState.Online, (DeviceAttributes.Paper, level));
        Assert.Contains(_r.Reconcile(full, cur), e => e.Type == expected);
    }

    [Fact]
    public void SafeDoor_OpenThenClosed()
    {
        var closed = S(DeviceClass.CashDispenser, DeviceState.Online, (DeviceAttributes.SafeDoor, "closed"));
        var open = S(DeviceClass.CashDispenser, DeviceState.Online, (DeviceAttributes.SafeDoor, "open"));
        Assert.Contains(_r.Reconcile(closed, open), e => e.Type == DeviceEventType.SafeDoorOpen && e.Severity == AlertSeverity.Critical);
        Assert.Contains(_r.Reconcile(open, closed), e => e.Type == DeviceEventType.SafeDoorClosed);
    }

    [Fact]
    public void Sensors_Tamper_Vibration_Heat()
    {
        var quiet = S(DeviceClass.Sensors, DeviceState.Online, (DeviceAttributes.Tamper, "inactive"));
        var loud = S(DeviceClass.Sensors, DeviceState.Online, (DeviceAttributes.Tamper, "active"), (DeviceAttributes.Vibration, "active"), (DeviceAttributes.Heat, "active"));
        var types = _r.Reconcile(quiet, loud).Select(e => e.Type).ToList();
        Assert.Contains(DeviceEventType.TamperDetected, types);
        Assert.Contains(DeviceEventType.VibrationDetected, types);
        Assert.Contains(DeviceEventType.TemperatureHigh, types);
    }

    [Fact]
    public void Cash_EmptyAndLow_AreRaisedOnTransitionOnly()
    {
        var ok = new[] { CashCalculatorTests.U(1, 20, 500, min: 100) };
        var low = new[] { CashCalculatorTests.U(1, 20, 90, CashUnitStatus.Low, min: 100) };
        var empty = new[] { CashCalculatorTests.U(1, 20, 0, CashUnitStatus.Empty, min: 100) };

        Assert.Single(_r.ReconcileCash("CDM", ok, low, T), e => e.Type == DeviceEventType.CashLow);
        Assert.Single(_r.ReconcileCash("CDM", low, empty, T), e => e.Type == DeviceEventType.CashEmpty);
        Assert.Empty(_r.ReconcileCash("CDM", empty, empty, T));
        Assert.Empty(_r.ReconcileCash("CDM", low, low, T));
    }
}
