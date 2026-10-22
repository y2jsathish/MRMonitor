using AtmNexus.Agent;
using AtmNexus.Agent.Cache;
using AtmNexus.Agent.Platform;
using AtmNexus.Agent.Workers;
using AtmNexus.Simulator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace AtmNexus.Tests;

/// <summary>
/// End-to-end: the REAL hosted agent (XFS listener, poller, alarm pipeline, health service, outbox) running against the
/// simulated ATM. Scenarios are injected as XFS events and as physical state changes, exactly like hardware would.
/// </summary>
public sealed class AgentPipelineTests : IAsyncLifetime
{
    private IHost _host = null!;
    private SimulatedAtm _atm = null!;
    private ScenarioEngine _scenarios = null!;
    private IOutbox _outbox = null!;
    private AgentStateStore _state = null!;

    public async Task InitializeAsync()
    {
        _atm = new SimulatedAtm();
        var api = new MockXfsApi(_atm);
        var ejDir = Path.Combine(Path.GetTempPath(), "nexus-e2e-" + Guid.NewGuid().ToString("N"));
        _scenarios = new ScenarioEngine(_atm, api, new EjGenerator(ejDir, "ncr"));

        var cfg = new Dictionary<string, string?>
        {
            ["Agent:AtmCode"] = "SIM-E2E",
            ["Agent:GatewayBaseUrl"] = "http://127.0.0.1:1",         // unreachable on purpose: events must stay queued
            ["Agent:CachePath"] = ":memory:",
            ["Agent:DevicePollInterval"] = "00:00:00.200",
            ["Agent:HealthInterval"] = "00:00:00.300",
            ["Agent:ServicePollInterval"] = "00:00:00.300",
            ["Agent:TelemetryInterval"] = "00:10:00",
            ["Agent:CommandLongPoll"] = "00:10:00",
            ["Alarms:DedupWindow"] = "00:00:30",
        };
        void Dev(int i, string n, DeviceClass c, params (string, string)[] s)
        {
            cfg[$"DeviceManager:Devices:{i}:LogicalName"] = n;
            cfg[$"DeviceManager:Devices:{i}:Class"] = c.ToString();
            cfg[$"DeviceManager:Devices:{i}:Adapter"] = "simulator";
            foreach (var (k, v) in s) cfg[$"DeviceManager:Devices:{i}:Settings:{k}"] = v;
        }
        Dev(0, "CDM", DeviceClass.CashDispenser, ("AllowDispense", "true"));
        Dev(1, "IDC", DeviceClass.CardReader);
        Dev(2, "PTR", DeviceClass.Printer);
        Dev(3, "PIN", DeviceClass.PinPad);
        Dev(4, "SIU", DeviceClass.Sensors);

        var b = Host.CreateApplicationBuilder();
        b.Configuration.AddInMemoryCollection(cfg);
        b.Logging.ClearProviders();
        b.Services.AddNexusAgentCore(b.Configuration);
        b.Services.AddSingleton<IXfsApi>(api);
        b.Services.AddSingleton<IWindowsServiceController, FakeServiceController>();
        b.Services.AddSingleton<IProcessRunner, FakeProcessRunner>();
        b.Services.AddSingleton<INetworkProbe, FakeNetworkProbe>();
        b.Services.AddSingleton<IWindowsEventSource, EmptyWindowsEventSource>();
        b.Services.AddSingleton<ISoftwareInventory, FakeSoftwareInventory>();
        b.Services.AddSingleton<IPatchInventory, FakePatchInventory>();
        b.Services.AddSingleton<IAtmActivityProbe, SettableIdleProbe>();
        b.Services.AddSingleton<ILogBundleUploader, NoopLogBundleUploader>();
        b.Services.AddSingleton<IAgentTokenProvider, NullTokenProvider>();

        _host = b.Build();
        await _host.StartAsync();
        _outbox = _host.Services.GetRequiredService<IOutbox>();
        _state = _host.Services.GetRequiredService<AgentStateStore>();
        await Eventually(() => _state.Devices().Count == 5, "initial device poll");
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static async Task Eventually(Func<bool> condition, string what, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail($"Timed out waiting for: {what}");
    }

    private async Task<List<AgentEvent>> QueuedEvents() =>
        (await _outbox.PeekAsync("event", 1000, default))
        .Select(i => System.Text.Json.JsonSerializer.Deserialize<AgentEvent>(i.Json, ContractJson.Options)!).ToList();

    private async Task WaitForEvent(string code)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if ((await QueuedEvents()).Any(e => e.Code == code)) return;
            await Task.Delay(50);
        }
        Assert.Fail($"Event {code} was not queued. Queued: {string.Join(", ", (await QueuedEvents()).Select(e => e.Code))}");
    }

    [Fact]
    public async Task HealthyAtm_ProducesHealthyScore_AndNoAlarms()
    {
        await Eventually(() => _state.LastHealth is not null, "first health evaluation");
        Assert.Equal(HealthLevel.Healthy, _state.LastHealth!.Level);
        Assert.Equal(100, _state.LastHealth.Score);
        Assert.DoesNotContain(await QueuedEvents(), e => e.Severity >= AlertSeverity.Warning);
    }

    [Fact]
    public async Task SafeDoorOpen_FlowsFromXfsEvent_ToQueuedCriticalAlert_AndTanksHealth()
    {
        _scenarios.SafeDoor(open: true);

        await WaitForEvent("SAFE_DOOR_OPEN");
        var ev = (await QueuedEvents()).First(e => e.Code == "SAFE_DOOR_OPEN");
        Assert.Equal(AlertSeverity.Critical, ev.Severity);

        await Eventually(() => _state.LastHealth is { Score: < 85 }, "health to drop");
        Assert.Contains(_state.LastHealth!.Reasons, r => r.Contains("Safe door"));
    }

    [Fact]
    public async Task CardJam_IsDetectedByXfsEvent_AndByPolling()
    {
        _scenarios.JamCard();
        await WaitForEvent("HARDWARE_ERROR");        // from WFS_SYSE_HARDWARE_ERROR
        await WaitForEvent("CARD_JAM");              // from the status poll (safety net)
        await Eventually(() => _state.GetDevice("IDC") is { Attributes: var a } && a[DeviceAttributes.Media] == "Jammed", "IDC state");
    }

    [Fact]
    public async Task CashEmpty_OnTheOnlyLargeCassette_DegradesCashHealth_AndAlerts()
    {
        _scenarios.CashEmpty(3);
        await WaitForEvent("CASH_EMPTY");
        await Eventually(() => _state.LastHealth is { } h && h.Components["cash"] < 100, "cash health to drop");
    }

    [Fact]
    public async Task AllCassettesEmpty_MeansCannotDispense_CriticalHealth()
    {
        foreach (var n in new[] { 1, 2, 3 }) _scenarios.CashEmpty(n);
        await Eventually(() => _state.LastHealth is { Score: <= 50 }, "cap at 50");
        Assert.Contains(_state.LastHealth!.Reasons, r => r.Contains("cannot dispense"));
    }

    [Fact]
    public async Task Tamper_RaisesCriticalAlert_AndCapsHealth()
    {
        _scenarios.Tamper(true);
        await WaitForEvent("TAMPER_DETECTED");
        await Eventually(() => _state.LastHealth is { Score: <= 40 }, "tamper cap");
    }

    [Fact]
    public async Task RepeatedXfsEvents_AreDeduplicated()
    {
        for (int i = 0; i < 5; i++) _scenarios.Vibration(true);
        await WaitForEvent("VIBRATION_DETECTED");
        await Task.Delay(500);
        Assert.Equal(1, (await QueuedEvents()).Count(e => e.Code == "VIBRATION_DETECTED" && e.Source == "SIU"));
    }

    [Fact]
    public async Task HealthLevelChange_EmitsOneEvent()
    {
        await Eventually(() => _state.LastHealth is not null, "baseline health");
        _scenarios.Tamper(true);
        await WaitForEvent("HEALTH_LEVEL_CHANGED");
        var changed = (await QueuedEvents()).First(e => e.Code == "HEALTH_LEVEL_CHANGED");
        Assert.Equal("Healthy", changed.Data!["previous"]);
    }

    [Fact]
    public async Task Dispense_ThroughTheDeviceManager_UpdatesCounters_AndLowCashAlerts()
    {
        var cdm = _host.Services.GetRequiredService<IDeviceManager>().Get<ICashDispenser>("CDM")!;
        _atm.Units.Single(u => u.Value == 100).Count = 101;                           // minimum is 100

        var r = await cdm.DispenseAsync(new DispenseRequest("USD", 100), default);
        Assert.True(r.Success);
        await WaitForEvent("CASH_LOW");                                                // raised by WFS_USRE_CDM_CASHUNITTHRESHOLD
    }

    [Fact]
    public async Task EventsStayQueued_WhileTheGatewayIsUnreachable()
    {
        _scenarios.SafeDoor(true);
        await WaitForEvent("SAFE_DOOR_OPEN");
        Assert.True(await _outbox.CountAsync(default) > 0);                            // store-and-forward
    }
}

public class AlarmPipelineTests
{
    private sealed class Signal : IUploadSignal
    {
        public int Triggers;
        public void Trigger() => Triggers++;
        public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => Task.CompletedTask;
    }

    private static AgentEvent Ev(string code, AlertSeverity s, string source = "CDM", string? unit = null) =>
        new(Guid.NewGuid(), code, s, source, "m", DateTimeOffset.UtcNow, unit is null ? null : new Dictionary<string, string> { ["unit"] = unit });

    [Fact]
    public async Task DedupWindow_SuppressesRepeats_ThenAllowsAfterExpiry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new SqliteAgentCache(":memory:", clock);
        var signal = new Signal();
        var pipe = new AlarmPipeline(cache, signal, Options.Create(new AlarmOptions { DedupWindow = TimeSpan.FromMinutes(5) }), clock, NullLogger<AlarmPipeline>.Instance);

        Assert.True(await pipe.SubmitAsync(Ev("CASH_LOW", AlertSeverity.Major, unit: "2"), default));
        Assert.False(await pipe.SubmitAsync(Ev("CASH_LOW", AlertSeverity.Major, unit: "2"), default));    // same unit: duplicate
        Assert.True(await pipe.SubmitAsync(Ev("CASH_LOW", AlertSeverity.Major, unit: "3"), default));     // other unit: distinct
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True(await pipe.SubmitAsync(Ev("CASH_LOW", AlertSeverity.Major, unit: "2"), default));
        Assert.Equal(3, await cache.CountAsync(default));
        Assert.Equal(3, signal.Triggers);                                                               // Major+ wakes the uploader
    }

    [Fact]
    public async Task FlappingSource_ProducesOneFlappingEvent()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new SqliteAgentCache(":memory:", clock);
        var pipe = new AlarmPipeline(cache, new Signal(),
            Options.Create(new AlarmOptions { DedupWindow = TimeSpan.FromSeconds(1), FlapThreshold = 4, FlapWindow = TimeSpan.FromMinutes(10) }),
            clock, NullLogger<AlarmPipeline>.Instance);

        for (int i = 0; i < 6; i++)
        {
            await pipe.SubmitAsync(Ev("SERVICE_NOT_RUNNING", AlertSeverity.Major, "services"), default);
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        var all = (await cache.PeekAsync("event", 100, default)).Select(i => i.Json).ToList();
        Assert.Equal(1, all.Count(j => j.Contains("\"FLAPPING\"")));
    }

    [Fact]
    public async Task Outbox_SurvivesReopen_AndAcksRemoveRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus-{Guid.NewGuid():N}.db");
        try
        {
            using (var c1 = new SqliteAgentCache(path))
            {
                await c1.EnqueueAsync("event", "{\"a\":1}", default);
                await c1.EnqueueAsync("event", "{\"a\":2}", default);
                await c1.SetAsync("ej:x", 123, "fp", default);
            }
            using var c2 = new SqliteAgentCache(path);
            var items = await c2.PeekAsync("event", 10, default);
            Assert.Equal(2, items.Count);
            Assert.Equal((123L, "fp"), (await c2.GetAsync("ej:x", default))!.Value);
            await c2.AckAsync([items[0].Id], default);
            Assert.Equal(1, await c2.CountAsync(default));
            await c2.TrimAsync(0, default);
            Assert.Equal(0, await c2.CountAsync(default));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) { try { File.Delete(f); } catch (IOException) { } }
        }
    }

    [Fact]
    public async Task NonceStore_RejectsReplay_UntilExpiry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new SqliteAgentCache(":memory:", clock);
        Assert.True(await cache.TryRegisterAsync("n1", clock.GetUtcNow().AddMinutes(1), default));
        Assert.False(await cache.TryRegisterAsync("n1", clock.GetUtcNow().AddMinutes(1), default));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await cache.TryRegisterAsync("n1", clock.GetUtcNow().AddMinutes(1), default));   // expired nonce row was pruned
    }

    [Fact]
    public void AlarmCodes_AreScreamingSnake()
    {
        Assert.Equal("CASH_LOW", AlarmProcessor.ToCode(DeviceEventType.CashLow));
        Assert.Equal("SAFE_DOOR_OPEN", AlarmProcessor.ToCode(DeviceEventType.SafeDoorOpen));
        Assert.Equal("TAMPER_DETECTED", AlarmProcessor.ToCode(DeviceEventType.TamperDetected));
    }
}
