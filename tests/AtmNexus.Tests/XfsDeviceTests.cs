using System.Net;
using AtmNexus.Devices.Xfs.Xfs4Iot;
using AtmNexus.Simulator;

namespace AtmNexus.Tests;

/// <summary>The real device services + XfsServiceManager running on the mock XFS provider (no hardware, no Windows).</summary>
public sealed class XfsDeviceTests : IDisposable
{
    private readonly SimulatedAtm _atm = new();
    private readonly MockXfsApi _api;
    private readonly XfsServiceManager _xfs;

    public XfsDeviceTests()
    {
        _api = new MockXfsApi(_atm);
        _xfs = new XfsServiceManager(_api, Options.Create(new XfsServiceManagerOptions { BaseRetryDelay = TimeSpan.FromMilliseconds(1) }),
            Options.Create(new XfsApiOptions()), NullLogger<XfsServiceManager>.Instance);
    }

    public void Dispose() => _xfs.Dispose();

    private static DeviceDescriptor Desc(string name, DeviceClass c, params (string, string)[] settings) =>
        new() { LogicalName = name, Class = c, Adapter = "simulator", Settings = settings.ToDictionary(s => s.Item1, s => s.Item2) };

    private async Task<CashDispenserService> Cdm(bool allowDispense = true)
    {
        var d = Desc("CDM", DeviceClass.CashDispenser);
        var svc = new CashDispenserService(new Xfs3CdmBackend(_xfs, d), d,
            new CashDispenserOptions { AllowDispense = allowDispense, MaxDispenseAmount = 1000 }, NullLogger<CashDispenserService>.Instance);
        await svc.OpenAsync(default);
        return svc;
    }

    // ------------------------------------------------------------ cash dispenser
    [Fact]
    public async Task CashUnits_AreReadFromTheServiceProvider()
    {
        var cdm = await Cdm();
        var units = await cdm.GetCashUnitsAsync(default);
        Assert.Equal(4, units.Count);
        Assert.Equal(CashUnitType.Reject, units.Single(u => u.Number == 4).Type);
        Assert.Equal([20, 50, 100], units.Where(u => u.Type == CashUnitType.Dispense).Select(u => u.Denomination).ToArray());
        Assert.Equal(1000L * 20 + 800 * 50 + 500 * 100, await cdm.CalculateRemainingCashAsync("USD", default));
    }

    [Fact]
    public async Task Dispense_Disabled_ByDefault()
    {
        var cdm = await Cdm(allowDispense: false);
        var r = await cdm.DispenseAsync(new DispenseRequest("USD", 100), default);
        Assert.False(r.Success);
        Assert.Contains("disabled", r.Error);
        Assert.Equal(1000, (await cdm.GetCashUnitsAsync(default)).First().Count);
    }

    [Fact]
    public async Task Dispense_ReportsNotesFromCounterDelta()
    {
        var cdm = await Cdm();
        var r = await cdm.DispenseAsync(new DispenseRequest("USD", 170), default);
        Assert.True(r.Success);
        Assert.Equal(170, r.DispensedAmount);
        Assert.Equal([(100, 1), (50, 1), (20, 1)], r.Notes.OrderByDescending(n => n.Denomination).Select(n => (n.Denomination, n.Count)).ToArray());
        Assert.Equal(499, (await cdm.GetCashUnitsAsync(default)).Single(u => u.Denomination == 100).Count);
    }

    [Theory]
    [InlineData(15)]      // not composable
    [InlineData(0)]
    [InlineData(5000)]    // above MaxDispenseAmount
    public async Task Dispense_RejectsInvalidOrImpossibleAmounts_WithoutTouchingTheDevice(long amount)
    {
        var cdm = await Cdm();
        var r = await cdm.DispenseAsync(new DispenseRequest("USD", amount), default);
        Assert.False(r.Success);
        Assert.Equal(0, r.DispensedAmount);
        Assert.Equal(1000, (await cdm.GetCashUnitsAsync(default)).First().Count);
    }

    [Fact]
    public async Task Dispense_DeviceFault_IsNotRetried_AndReconciles()
    {
        var cdm = await Cdm();
        _atm.DeviceState = 4;                       // hardware error: mock returns -1004 for DISPENSE
        var r = await cdm.DispenseAsync(new DispenseRequest("USD", 100), default);
        Assert.False(r.Success);
        Assert.Contains("-1004", r.Error);
        Assert.Equal(0, r.DispensedAmount);
        Assert.Equal(500, (await cdm.GetCashUnitsAsync(default)).Single(u => u.Denomination == 100).Count);
    }

    [Fact]
    public async Task EmptyingACassette_RaisesThresholdEvent_ThatMapsToCashEmpty()
    {
        var cdm = await Cdm();
        var events = new List<XfsEvent>();
        _xfs.EventReceived += events.Add;
        _atm.Units.Single(u => u.Value == 100).Count = 1;        // one note left, minimum is 100 => already "low"

        await cdm.DispenseAsync(new DispenseRequest("USD", 100), default);   // uses the last note

        var ev = Assert.Single(events);
        Assert.Equal(XfsEventKind.User, ev.Kind);
        var mapped = new XfsEventProcessor().Process(ev)!;
        Assert.Equal(DeviceEventType.CashEmpty, mapped.Type);
        Assert.Equal(AlertSeverity.Critical, mapped.Severity);
    }

    [Fact]
    public async Task Status_ReflectsSafeDoorAndHardwareState()
    {
        var cdm = await Cdm();
        Assert.Equal("closed", (await cdm.GetStatusAsync(default)).Attributes[DeviceAttributes.SafeDoor]);
        _atm.SafeDoorOpen = true;
        _atm.DeviceState = 4;
        var st = await cdm.GetStatusAsync(default);
        Assert.Equal("open", st.Attributes[DeviceAttributes.SafeDoor]);
        Assert.Equal(DeviceState.HardwareError, st.State);
    }

    // ------------------------------------------------------------ card reader
    private async Task<CardReaderService> Idc(params (string, string)[] settings)
    {
        var d = Desc("IDC", DeviceClass.CardReader, settings);
        var svc = new CardReaderService(_xfs, d, NullLogger<CardReaderService>.Instance);
        await svc.OpenAsync(default);
        return svc;
    }

    private static CardReadOptions Read(bool raw = false) => new(false, true, false, true, TimeSpan.FromSeconds(5), raw);

    [Fact]
    public async Task ReadCard_ReturnsOnlyMaskedData_ChipAndEmvDetected()
    {
        var idc = await Idc();
        _atm.CardPresent = true;
        var r = await idc.ReadCardAsync(Read(), default);

        Assert.True(r.CardPresent);
        Assert.True(r.ChipPresent);
        Assert.True(r.IsEmv);
        Assert.False(r.TechnicalFallbackSuspected);
        Assert.Equal("411111******1111", r.MaskedPan);
        Assert.Equal("201", r.ServiceCode);
        Assert.Null(r.RawTracks);
        Assert.DoesNotContain("4111111111111111", string.Join("|", r.Tracks.Select(t => t.ToString())) + r.MaskedPan);
    }

    [Fact]
    public async Task ChipFailureOnChipCard_FlagsTechnicalFallback()
    {
        var idc = await Idc();
        _atm.CardPresent = true;
        _atm.ChipFailure = true;
        var r = await idc.ReadCardAsync(Read(), default);
        Assert.False(r.ChipPresent);
        Assert.True(r.TechnicalFallbackSuspected);       // service code 201 says "chip", chip unreadable: skimming / fallback indicator
    }

    [Fact]
    public async Task ChipNotRequested_NeverFlagsFallback()
    {
        var idc = await Idc();
        _atm.CardPresent = true;
        var r = await idc.ReadCardAsync(new CardReadOptions(false, true, false, false, TimeSpan.FromSeconds(5)), default);
        Assert.False(r.TechnicalFallbackSuspected);
    }

    [Fact]
    public async Task RawTracks_AreReturnedOnlyForAllowListedTestBins()
    {
        _atm.CardPresent = true;

        var denied = await (await Idc()).ReadCardAsync(Read(raw: true), default);
        Assert.Null(denied.RawTracks);

        var allowed = await (await Idc(("AllowRawTracks", "true"), ("TestBins", "411111"))).ReadCardAsync(Read(raw: true), default);
        var raw = Assert.Single(allowed.RawTracks!);
        Assert.DoesNotContain("4111", raw.Value.ToString());          // never printable
        raw.Value.Dispose();
    }

    [Fact]
    public async Task JammedReader_ThrowsXfsException_AndReportsJamInStatus()
    {
        var idc = await Idc();
        _atm.CardPresent = true;
        _atm.CardJammed = true;
        await Assert.ThrowsAsync<XfsException>(() => idc.ReadCardAsync(Read(), default));
        Assert.Equal("Jammed", (await idc.GetStatusAsync(default)).Attributes[DeviceAttributes.Media]);
        await idc.ResetAsync(default);
        Assert.NotEqual("Jammed", (await idc.GetStatusAsync(default)).Attributes[DeviceAttributes.Media]);
    }

    [Fact]
    public async Task Retain_IncrementsRetainedCards()
    {
        var idc = await Idc();
        _atm.CardPresent = true;
        await idc.RetainAsync(default);
        Assert.Equal(1, (await idc.GetCardReaderStatusAsync(default)).RetainedCards);
    }

    // ------------------------------------------------------------ printer / epp / siu
    [Fact]
    public async Task Printer_ReportsWorstPaperLevel_AndJam()
    {
        var d = Desc("PTR", DeviceClass.Printer);
        var ptr = new PrinterService(_xfs, d, NullLogger<PrinterService>.Instance);
        await ptr.OpenAsync(default);

        Assert.Equal(SupplyLevel.Full, (await ptr.GetPrinterStatusAsync(default)).Paper);
        _atm.PaperLevel = 2;
        Assert.Equal(SupplyLevel.Out, (await ptr.GetPrinterStatusAsync(default)).Paper);
        _atm.PaperJammed = true;
        var st = await ptr.GetStatusAsync(default);
        Assert.Equal("Jammed", st.Attributes[DeviceAttributes.Paper]);
    }

    [Fact]
    public async Task PinPad_TamperIsVisible()
    {
        var d = Desc("PIN", DeviceClass.PinPad);
        var pin = new PinpadService(_xfs, d, NullLogger<PinpadService>.Instance);
        await pin.OpenAsync(default);
        Assert.False((await pin.GetEppInfoAsync(default)).TamperSuspected);
        _atm.EppState = 6;
        Assert.True((await pin.GetEppInfoAsync(default)).TamperSuspected);
        Assert.Equal("Tampered", (await pin.GetStatusAsync(default)).Attributes[DeviceAttributes.Encryptor]);
    }

    [Fact]
    public async Task Siu_ReadsDoorsAndSensors_UsingTheSensorMap()
    {
        var d = Desc("SIU", DeviceClass.Sensors);
        var siu = new SiuService(_xfs, d, SiuSensorMap.Default, NullLogger<SiuService>.Instance);
        await siu.OpenAsync(default);

        _atm.SafeDoorOpen = true;
        _atm.TamperActive = true;
        var sensors = await siu.GetSensorsAsync(default);
        Assert.Equal(SensorState.Active, sensors.Single(s => s.Kind == SensorKind.SafeDoor).State);
        Assert.Equal(SensorState.Active, sensors.Single(s => s.Kind == SensorKind.Tamper).State);
        Assert.Equal(SensorState.Inactive, sensors.Single(s => s.Kind == SensorKind.CabinetDoor).State);

        var st = await siu.GetStatusAsync(default);
        Assert.Equal("open", st.Attributes[DeviceAttributes.SafeDoor]);
        Assert.Equal("active", st.Attributes[DeviceAttributes.Tamper]);
    }

    [Fact]
    public async Task Siu_VendorSpecificMap_ReadsVibrationAtDifferentIndex()
    {
        var map = new SiuSensorMap([new(1, 6, SensorKind.Vibration, "Seismic", 0x0001)]);
        var api = new MockXfsApi(_atm, map);
        using var xfs = new XfsServiceManager(api, Options.Create(new XfsServiceManagerOptions()), Options.Create(new XfsApiOptions()), NullLogger<XfsServiceManager>.Instance);
        var siu = new SiuService(xfs, Desc("SIU", DeviceClass.Sensors), map, NullLogger<SiuService>.Instance);
        await siu.OpenAsync(default);
        _atm.VibrationActive = true;
        Assert.Equal(SensorState.Active, (await siu.GetSensorsAsync(default)).Single().State);
    }

    // ------------------------------------------------------------ service manager behaviour
    [Fact]
    public async Task Manager_RetriesGetInfo_ButNeverExecute()
    {
        var flaky = new FlakyApi(_api);
        using var mgr = new XfsServiceManager(flaky, Options.Create(new XfsServiceManagerOptions { MaxInfoAttempts = 3, BaseRetryDelay = TimeSpan.FromMilliseconds(1) }),
            Options.Create(new XfsApiOptions()), NullLogger<XfsServiceManager>.Instance);
        var h = await mgr.OpenAsync("CDM", DeviceClass.CashDispenser, default);

        flaky.FailNextGetInfo = 2;
        Assert.True((await mgr.GetInfoAsync(h, XfsIds.Cdm.InfoStatus, null, default)).Success);
        Assert.Equal(3, flaky.GetInfoCalls);                          // 2 failures + 1 success

        flaky.FailNextExecute = 1;
        var r = await mgr.ExecuteAsync(h, XfsIds.Cdm.CmdPresent, null, TimeSpan.FromSeconds(5), retryable: false, default);
        Assert.False(r.Success);
        Assert.Equal(1, flaky.ExecuteCalls);                          // dispense-class commands are never auto-retried
    }

    private sealed class FlakyApi(IXfsApi inner) : IXfsApi
    {
        public int FailNextGetInfo, FailNextExecute, GetInfoCalls, ExecuteCalls;
        public event Action<XfsApiEvent>? EventReceived { add => inner.EventReceived += value; remove => inner.EventReceived -= value; }
        public void StartUp() => inner.StartUp();
        public XfsOpenInfo Open(string n, DeviceClass c, TimeSpan t) => inner.Open(n, c, t);
        public int Close(ushort h) => inner.Close(h);
        public int Register(ushort h, XfsEventClass c) => inner.Register(h, c);
        public int Deregister(ushort h, XfsEventClass c) => inner.Deregister(h, c);
        public int Lock(ushort h, TimeSpan t) => inner.Lock(h, t);
        public int Unlock(ushort h) => inner.Unlock(h);
        public XfsResponse GetInfo(ushort h, uint cat, object? q, TimeSpan t)
        {
            GetInfoCalls++;
            return FailNextGetInfo-- > 0 ? new XfsResponse(-99, null) : inner.GetInfo(h, cat, q, t);
        }
        public XfsResponse Execute(ushort h, uint cmd, object? d, TimeSpan t)
        {
            ExecuteCalls++;
            return FailNextExecute-- > 0 ? new XfsResponse(-99, null) : inner.Execute(h, cmd, d, t);
        }
        public void Dispose() { }
    }

    // ------------------------------------------------------------ XFS4IoT bridge (same business logic, different transport)
    [Fact]
    public async Task Xfs4Iot_Backend_BehavesLikeXfs3_ThroughTheBridge()
    {
        var server = new MockXfs4IotServer(_atm, 15846);
        try { server.Start(); }
        catch (HttpListenerException) { return; }     // sandbox forbids local listeners (e.g. Windows without urlacl): skip

        await using (server)
        {
            var d = Desc("IOT_CDM", DeviceClass.CashDispenser);
            var backend = new Xfs4IotCdmBackend(new Xfs4IotClient(server.Endpoint, NullLogger<Xfs4IotClient>.Instance), d, NullLogger<Xfs4IotCdmBackend>.Instance);
            await using var cdm = new CashDispenserService(backend, d, new CashDispenserOptions { AllowDispense = true, MaxDispenseAmount = 1000 }, NullLogger<CashDispenserService>.Instance);
            await cdm.OpenAsync(default);

            Assert.Equal("xfs4iot", cdm.AdapterName);
            Assert.Equal(DeviceState.Online, (await cdm.GetStatusAsync(default)).State);
            Assert.Equal(3, (await cdm.GetCashUnitsAsync(default)).Count(u => u.Type == CashUnitType.Dispense));

            var r = await cdm.DispenseAsync(new DispenseRequest("USD", 170), default);
            Assert.True(r.Success);
            Assert.Equal(170, r.DispensedAmount);

            var raised = new TaskCompletionSource<DeviceEvent>();
            cdm.EventRaised += (_, e) => raised.TrySetResult(e);
            await server.Push("CashManagement.CashUnitThresholdEvent", new { unit = "unit2", status = "empty" });
            var ev = await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(DeviceEventType.CashEmpty, ev.Type);
        }
    }
}
