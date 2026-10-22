namespace AtmNexus.Tests;

public class CashCalculatorTests
{
    internal static CashUnit U(int n, int den, int count, CashUnitStatus st = CashUnitStatus.Ok, CashUnitType t = CashUnitType.Dispense,
        string cur = "USD", int min = 10, bool locked = false) =>
        new(n, $"C{n}", $"Cassette {n}", t, cur, den, count, count, 0, min, 2000, st, locked);

    [Fact]
    public void RemainingCash_CountsOnlyDispensableUnits()
    {
        var units = new[]
        {
            U(1, 20, 100),
            U(2, 50, 10, CashUnitStatus.Empty),                 // empty status: excluded
            U(3, 100, 5, locked: true),                         // app-locked: excluded
            U(4, 0, 30, t: CashUnitType.Reject),                // reject bin: never dispensable
            U(5, 10, 7, cur: "EUR")                             // other currency
        };
        Assert.Equal(2000, CashCalculator.RemainingCash(units, "USD"));
        Assert.Equal(70, CashCalculator.RemainingCash(units, "EUR"));
    }

    [Theory]
    [InlineData(70, true)]    // 20 + 50
    [InlineData(60, true)]    // 3 x 20
    [InlineData(110, true)]   // 3 x 20 + 50
    [InlineData(30, false)]   // 10 is not composable from {20,50}
    [InlineData(10, false)]
    [InlineData(130, false)]  // would need 4 x 20 + 50 but only 3 x 20 available
    [InlineData(0, false)]
    public void CanDispense_UsesAvailableNotesExactly(long amount, bool expected)
    {
        var units = new[] { U(1, 20, 3), U(2, 50, 1) };
        Assert.Equal(expected, CashCalculator.CanDispense(units, "USD", amount));
    }

    [Fact]
    public void CanDispense_IgnoresRejectAndWrongCurrency()
    {
        var units = new[] { U(1, 100, 50, t: CashUnitType.Reject), U(2, 100, 50, cur: "EUR") };
        Assert.False(CashCalculator.CanDispense(units, "USD", 100));
    }

    [Fact]
    public void Summarize_FlagsLowEmptyAndFaulty()
    {
        var units = new[]
        {
            U(1, 20, 1000), U(2, 50, 8, min: 10), U(3, 100, 0, CashUnitStatus.Empty), U(4, 200, 50, CashUnitStatus.Inoperative)
        };
        var s = CashCalculator.Summarize(units);
        Assert.Equal(1, s.LowUnits);
        Assert.Equal(1, s.EmptyUnits);
        Assert.Equal(1, s.FaultyUnits);
        Assert.Equal(1000L * 20 + 8 * 50, s.ByCurrency["USD"].Total);
    }
}

public class CardParserTests
{
    [Fact]
    public void Atr_Valid_T0_IsEmvCompatible()
    {
        Assert.True(AtrParser.TryParse([0x3B, 0x65, 0x00, 0x00, 0x20, 0x63, 0xCB, 0xA3, 0xA0], out var atr));
        Assert.True(atr!.EmvCompatible);
        Assert.Single(atr.Protocols);
        Assert.Equal(0, atr.Protocols[0]);
    }

    [Fact]
    public void Atr_T1_RequiresValidTck()
    {
        Assert.True(AtrParser.TryParse([0x3B, 0x80, 0x01, 0x81], out var ok));
        Assert.Contains(1, ok!.Protocols);
        Assert.False(AtrParser.TryParse([0x3B, 0x80, 0x01, 0x82], out _));     // bad TCK
    }

    [Fact]
    public void Atr_ProtocolOutsideEmv_IsParsedButNotEmvCompatible()
    {
        Assert.True(AtrParser.TryParse([0x3B, 0x80, 0x0E, 0x8E], out var atr));
        Assert.False(atr!.EmvCompatible);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00 })]                 // bad TS
    [InlineData(new byte[] { 0x3B })]                       // too short
    [InlineData(new byte[] { 0x3B, 0x65, 0x00 })]           // truncated
    public void Atr_Invalid_IsRejected(byte[] bytes) => Assert.False(AtrParser.TryParse(bytes, out _));

    [Fact]
    public void Track2_ExtractsMaskedPanAndServiceCode_Only()
    {
        Assert.True(Track2Parser.TryParse(System.Text.Encoding.ASCII.GetBytes(";4111111111111111=29122011234567890?"), out var t));
        Assert.Equal("411111******1111", t!.MaskedPan);
        Assert.Equal("201", t.ServiceCode);
        Assert.True(t.ChipCardPerServiceCode);
        Assert.True(t.LuhnValid);
    }

    [Fact]
    public void Track2_ServiceCode101_IsNotAChipCard()
    {
        Assert.True(Track2Parser.TryParse(System.Text.Encoding.ASCII.GetBytes("5500000000000004=25121011234"), out var t));
        Assert.False(t!.ChipCardPerServiceCode);
    }

    [Fact]
    public void Track2_Garbage_IsRejected() => Assert.False(Track2Parser.TryParse("hello"u8, out _));

    [Fact]
    public void SensitiveBytes_NeverPrintsContent_AndWipesOnDispose()
    {
        var raw = new byte[] { 1, 2, 3, 4 };
        var s = new SensitiveBytes(raw);
        Assert.Equal("[REDACTED 4 bytes]", s.ToString());
        s.Dispose();
        Assert.All(raw, b => Assert.Equal(0, b));
    }
}

public class XfsEventProcessorTests
{
    private static readonly XfsEventProcessor P = new();

    private static DeviceEvent Run(DeviceClass cls, XfsEventKind kind, uint id, object? data = null) =>
        P.Process(new XfsEvent(kind, "SVC", cls, id, 0, data, DateTimeOffset.UtcNow))!;

    private static XfsCashUnitData Unit(ushort status, uint count) =>
        new(2, 3, "Cassette 2", "C2", "USD", 50, 800, count, 0, 100, 2000, false, status);

    [Fact]
    public void User_CashUnitThreshold_Empty_IsCritical()
    {
        var e = Run(DeviceClass.CashDispenser, XfsEventKind.User, 201, Unit(4, 0));
        Assert.Equal(DeviceEventType.CashEmpty, e.Type);
        Assert.Equal(AlertSeverity.Critical, e.Severity);
        Assert.Equal("2", e.Data["unit"]);
    }

    [Fact]
    public void User_CashUnitThreshold_Low_IsMajor()
    {
        var e = Run(DeviceClass.CashDispenser, XfsEventKind.User, 201, Unit(3, 80));
        Assert.Equal(DeviceEventType.CashLow, e.Type);
        Assert.Equal(AlertSeverity.Major, e.Severity);
        Assert.Contains("low", e.Message);
    }

    [Theory]
    [InlineData((ushort)1, DeviceEventType.PaperLow, AlertSeverity.Warning)]
    [InlineData((ushort)2, DeviceEventType.PaperEmpty, AlertSeverity.Major)]
    public void User_PaperThreshold_MapsLevel(ushort level, DeviceEventType type, AlertSeverity sev)
    {
        var e = Run(DeviceClass.Printer, XfsEventKind.User, 301, new XfsUShortData(level));
        Assert.Equal(type, e.Type);
        Assert.Equal(sev, e.Severity);
    }

    [Theory]
    [InlineData((ushort)2, (ushort)1, (ushort)0x0002, DeviceEventType.SafeDoorOpen, AlertSeverity.Critical)]
    [InlineData((ushort)2, (ushort)1, (ushort)0x0001, DeviceEventType.SafeDoorClosed, AlertSeverity.Info)]
    [InlineData((ushort)2, (ushort)0, (ushort)0x0002, DeviceEventType.CabinetDoorOpen, AlertSeverity.Major)]
    [InlineData((ushort)1, (ushort)1, (ushort)0x0001, DeviceEventType.TamperDetected, AlertSeverity.Critical)]
    [InlineData((ushort)1, (ushort)3, (ushort)0x0001, DeviceEventType.VibrationDetected, AlertSeverity.Major)]
    [InlineData((ushort)1, (ushort)4, (ushort)0x0001, DeviceEventType.TemperatureHigh, AlertSeverity.Major)]
    public void Service_SiuPortStatus_MapsSensors(ushort portType, ushort index, ushort status, DeviceEventType type, AlertSeverity sev)
    {
        var e = Run(DeviceClass.Sensors, XfsEventKind.Service, 801, new XfsSiuPortEventData(portType, index, status));
        Assert.Equal(type, e.Type);
        Assert.Equal(sev, e.Severity);
    }

    [Fact]
    public void Service_SiuPort_UsesVendorSensorMap()
    {
        // Hyosung-style wiring: vibration on index 6 instead of 3
        var map = new SiuSensorMap([new(1, 6, SensorKind.Vibration, "Seismic", 0x0001)]);
        var proc = new XfsEventProcessor(null, map);
        var e = proc.Process(new XfsEvent(XfsEventKind.Service, "SIU", DeviceClass.Sensors, 801, 0, new XfsSiuPortEventData(1, 6, 1), DateTimeOffset.UtcNow))!;
        Assert.Equal(DeviceEventType.VibrationDetected, e.Type);
    }

    [Fact]
    public void Service_CdmSafeDoor_And_IdcCardRetained()
    {
        Assert.Equal(DeviceEventType.SafeDoorOpen, Run(DeviceClass.CashDispenser, XfsEventKind.Service, 201).Type);
        Assert.Equal(DeviceEventType.CardRetained, Run(DeviceClass.CardReader, XfsEventKind.Service, 102).Type);
        Assert.Equal(DeviceEventType.CardInserted, Run(DeviceClass.CardReader, XfsEventKind.Execute, 103).Type);
    }

    [Theory]
    [InlineData(2u, DeviceEventType.HardwareError, AlertSeverity.Critical)]
    [InlineData(9u, DeviceEventType.FraudAttempt, AlertSeverity.Critical)]
    [InlineData(6u, DeviceEventType.SoftwareError, AlertSeverity.Major)]
    public void System_Events_AreClassIndependent(uint id, DeviceEventType type, AlertSeverity sev)
    {
        foreach (var cls in new[] { DeviceClass.CardReader, DeviceClass.CashDispenser, DeviceClass.PinPad })
        {
            var e = Run(cls, XfsEventKind.System, id);
            Assert.Equal(type, e.Type);
            Assert.Equal(sev, e.Severity);
        }
    }

    [Fact]
    public void System_DeviceStatus_HardwareErrorIsCritical()
    {
        var e = Run(DeviceClass.CashDispenser, XfsEventKind.System, 4, new XfsUShortData(4));
        Assert.Equal(DeviceEventType.DeviceStatusChanged, e.Type);
        Assert.Equal(AlertSeverity.Critical, e.Severity);
    }

    [Fact]
    public void Unmapped_Event_IsKeptAsInfoUnknown()
    {
        var e = Run(DeviceClass.Printer, XfsEventKind.Execute, 9999);
        Assert.Equal(DeviceEventType.Unknown, e.Type);
        Assert.Equal(AlertSeverity.Info, e.Severity);
    }

    [Fact]
    public void Catalog_ScopesDoNotLeak_BetweenClasses()
    {
        // id 201/User means "cash unit threshold" for CDM only; for a printer it is unmapped
        Assert.Equal(DeviceEventType.Unknown, Run(DeviceClass.Printer, XfsEventKind.User, 201).Type);
    }
}
