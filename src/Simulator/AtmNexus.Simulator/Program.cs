using AtmNexus.Agent;
using AtmNexus.Agent.Platform;
using AtmNexus.Simulator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ATM Nexus ATM simulator: runs the REAL agent pipeline against a mock XFS provider.
//   dotnet run --project src/Simulator/AtmNexus.Simulator -- --vendor ncr --atm SIM-0001 --gateway http://localhost:5080
// Vendors: ncr | dn | hyosung | xfs4iot

string Arg(string name, string fallback)
{
    int i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

string vendor = Arg("vendor", "ncr").ToLowerInvariant();
string atmCode = Arg("atm", "SIM-0001");
string gateway = Arg("gateway", "http://localhost:5080");
string ejDir = Path.Combine(AppContext.BaseDirectory, "sim-ej");
string cache = Path.Combine(Path.GetTempPath(), $"nexus-sim-{atmCode}.db");

var atm = new SimulatedAtm();
// Hyosung wires vibration to a different SIU index: demonstrates vendor-specific sensor maps.
var sensorMap = vendor == "hyosung"
    ? new SiuSensorMap([
        new(2, 0, SensorKind.CabinetDoor, "Cabinet door", 0x0002), new(2, 1, SensorKind.SafeDoor, "Safe door", 0x0002),
        new(1, 1, SensorKind.Tamper, "Tamper sensor", 0x0001), new(1, 6, SensorKind.Vibration, "Seismic sensor", 0x0001),
        new(1, 4, SensorKind.Temperature, "Heat sensor", 0x0001)])
    : SiuSensorMap.Default;

var xfsApi = new MockXfsApi(atm, sensorMap);
var ej = new EjGenerator(ejDir, vendor);
var scenarios = new ScenarioEngine(atm, xfsApi, ej, sensorMap);

MockXfs4IotServer? iot = null;
if (vendor == "xfs4iot") { iot = new MockXfs4IotServer(atm, 5846); iot.Start(); }

// ---------------- configuration (in-memory; the real agent uses appsettings.json) ----------------
string prefix = vendor switch { "ncr" => "NCR", "dn" => "DN", "hyosung" => "HYO", _ => "IOT" };
var cfg = new Dictionary<string, string?>
{
    ["Agent:AtmCode"] = atmCode, ["Agent:GatewayBaseUrl"] = gateway, ["Agent:CachePath"] = cache,
    ["Agent:DevicePollInterval"] = "00:00:05", ["Agent:HealthInterval"] = "00:00:10", ["Agent:TelemetryInterval"] = "00:00:10",
    ["Agent:ServicePollInterval"] = "00:00:10", ["Agent:CommandLongPoll"] = "00:00:10",
    ["Alarms:DedupWindow"] = "00:00:30",
    ["Ej:Interval"] = "00:00:05", ["Ej:Sources:0:Name"] = "sim", ["Ej:Sources:0:Vendor"] = vendor == "hyosung" ? "DieboldNixdorf" : vendor == "xfs4iot" ? "Generic" : vendor,
    ["Ej:Sources:0:Directory"] = ejDir, ["Ej:Sources:0:FilePattern"] = "*.jrn",
    ["Commands:AtmCode"] = atmCode,
};
void Dev(int i, string name, DeviceClass cls, string adapter = "simulator", string? endpoint = null, Dictionary<string, string>? settings = null)
{
    cfg[$"DeviceManager:Devices:{i}:LogicalName"] = name;
    cfg[$"DeviceManager:Devices:{i}:Class"] = cls.ToString();
    cfg[$"DeviceManager:Devices:{i}:Vendor"] = prefix;
    cfg[$"DeviceManager:Devices:{i}:Adapter"] = adapter;
    if (endpoint is not null) cfg[$"DeviceManager:Devices:{i}:Endpoint"] = endpoint;
    foreach (var (k, v) in settings ?? new()) cfg[$"DeviceManager:Devices:{i}:Settings:{k}"] = v;
}
var cashSettings = new Dictionary<string, string> { ["AllowDispense"] = "true", ["MaxDispenseAmount"] = "1000" };
if (vendor == "xfs4iot") Dev(0, $"{prefix}_CDM", DeviceClass.CashDispenser, "xfs4iot", iot!.Endpoint.ToString(), cashSettings);
else Dev(0, $"{prefix}_CDM", DeviceClass.CashDispenser, settings: cashSettings);
Dev(1, $"{prefix}_IDC", DeviceClass.CardReader);
Dev(2, $"{prefix}_PTR", DeviceClass.Printer);
Dev(3, $"{prefix}_PIN", DeviceClass.PinPad);
Dev(4, $"{prefix}_SIU", DeviceClass.Sensors);

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddInMemoryCollection(cfg);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("AtmNexus.Agent.Workers.AlarmPipeline", LogLevel.Debug);

builder.Services.AddNexusAgentCore(builder.Configuration);
builder.Services.AddSingleton<IXfsApi>(xfsApi);
builder.Services.AddSingleton(sensorMap);
builder.Services.AddSingleton(new XfsEventProcessor(null, sensorMap));   // replaces the default registration
builder.Services.AddSingleton<FakeServiceController>();
builder.Services.AddSingleton<IWindowsServiceController>(sp => sp.GetRequiredService<FakeServiceController>());
builder.Services.AddSingleton<IProcessRunner, FakeProcessRunner>();
builder.Services.AddSingleton<INetworkProbe, FakeNetworkProbe>();
builder.Services.AddSingleton<IWindowsEventSource, EmptyWindowsEventSource>();
builder.Services.AddSingleton<ISoftwareInventory, FakeSoftwareInventory>();
builder.Services.AddSingleton<IPatchInventory, FakePatchInventory>();
builder.Services.AddSingleton<IAtmActivityProbe, SettableIdleProbe>();
builder.Services.AddSingleton<ILogBundleUploader, NoopLogBundleUploader>();
builder.Services.AddSingleton<IAgentTokenProvider, NullTokenProvider>();

using var host = builder.Build();
await host.StartAsync();

var state = host.Services.GetRequiredService<AgentStateStore>();
var devices = host.Services.GetRequiredService<IDeviceManager>();
ICashDispenser Cash() => devices.GetAll<ICashDispenser>().First();

Console.WriteLine($"ATM Nexus simulator '{atmCode}' ({vendor}) -> {gateway}. Type 'help'.");
while (Console.ReadLine() is { } line)
{
    var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (p.Length == 0) continue;
    try
    {
        switch (p[0].ToLowerInvariant())
        {
            case "help":
                Console.WriteLine("card | take | retain | jam | chip-fault on|off | recover | empty N | low N | refill N | dispense AMT | safe open|close | cabinet open|close\n" +
                                  "tamper on|off | vibration on|off | paper full|low|out | epp-tamper | fault | ej TYPE AMT [completed|cancelled|timeout|failed]\n" +
                                  "status | cash | health | quit");
                break;
            case "card": scenarios.InsertCard(); break;
            case "take": scenarios.TakeCard(); break;
            case "retain": scenarios.RetainCard(); break;
            case "jam": scenarios.JamCard(); break;
            case "recover": scenarios.Recover(); break;
            case "chip-fault": scenarios.ChipFailure(p[1] == "on"); break;
            case "empty": scenarios.CashEmpty(int.Parse(p[1])); break;
            case "low": scenarios.CashLow(int.Parse(p[1])); break;
            case "refill": scenarios.Refill(int.Parse(p[1])); break;
            case "dispense":
            {
                var r = await Cash().DispenseAsync(new DispenseRequest("USD", long.Parse(p[1])), CancellationToken.None);
                Console.WriteLine(r.Success ? $"dispensed {r.DispensedAmount}: {string.Join(", ", r.Notes.Select(n => $"{n.Count}x{n.Denomination}"))}" : $"FAILED: {r.Error}");
                break;
            }
            case "safe": scenarios.SafeDoor(p[1] == "open"); break;
            case "cabinet": scenarios.CabinetDoor(p[1] == "open"); break;
            case "tamper": scenarios.Tamper(p[1] == "on"); break;
            case "vibration": scenarios.Vibration(p[1] == "on"); break;
            case "paper": scenarios.Paper(p[1] switch { "low" => (ushort)1, "out" => (ushort)2, _ => (ushort)0 }); break;
            case "epp-tamper": scenarios.EppTamper(); break;
            case "fault": scenarios.HardwareFault(); break;
            case "ej":
                Console.WriteLine(scenarios.EjTransaction(p.ElementAtOrDefault(1) ?? "WITHDRAWAL", decimal.Parse(p.ElementAtOrDefault(2) ?? "100"),
                    Enum.Parse<EjScenario>(p.ElementAtOrDefault(3) ?? "Completed", true)));
                break;
            case "status":
                foreach (var d in state.Devices()) Console.WriteLine($"{d.LogicalName,-12} {d.State,-14} {string.Join(", ", d.Attributes.Select(a => $"{a.Key}={a.Value}"))}");
                break;
            case "cash":
                foreach (var u in await Cash().GetCashUnitsAsync(CancellationToken.None))
                    Console.WriteLine($"unit {u.Number}: {u.Currency} {u.Denomination,4} x {u.Count,5}  {u.Status}");
                break;
            case "health":
                var h = state.LastHealth;
                Console.WriteLine(h is null ? "not computed yet" : $"{h.Score} {h.Level}  [{string.Join(", ", h.Components.Select(c => $"{c.Key}={c.Value}"))}]\n  {string.Join("\n  ", h.Reasons)}");
                break;
            case "quit": goto done;
            default: Console.WriteLine("unknown command; type 'help'"); break;
        }
    }
    catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or InvalidOperationException or ArgumentException)
    {
        Console.WriteLine($"error: {ex.Message}");
    }
}
done:
await host.StopAsync();
if (iot is not null) await iot.DisposeAsync();
