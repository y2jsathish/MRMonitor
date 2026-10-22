using AtmNexus.Agent;
using AtmNexus.Agent.Devices;
using AtmNexus.Agent.Platform;
using AtmNexus.Agent.Platform.Windows;
using AtmNexus.Devices.Xfs;
using AtmNexus.Devices.Xfs.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "ATMNexusAgent");
builder.Configuration.AddJsonFile(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ATMNexus", "agent.local.json"),
    optional: true, reloadOnChange: false);   // per-ATM overrides written by the installer (AtmCode, thumbprint, device list)

builder.Services.Configure<AgentSecurityOptions>(builder.Configuration.GetSection(AgentSecurityOptions.Section));

// ---- platform-neutral pipeline (shared with the simulator) ----
builder.Services.AddNexusAgentCore(builder.Configuration, gateway =>
    gateway.ConfigurePrimaryHttpMessageHandler(sp =>
        AgentHttp.CreateMtlsHandler(sp.GetRequiredService<IOptions<AgentSecurityOptions>>().Value)));
builder.Services.AddHttpClient("token").ConfigurePrimaryHttpMessageHandler(sp =>
    AgentHttp.CreateMtlsHandler(sp.GetRequiredService<IOptions<AgentSecurityOptions>>().Value));

// ---- Windows / XFS implementations ----
builder.Services.AddSingleton<IXfsApi, Win32XfsApi>();
builder.Services.AddSingleton<IDeviceDiscovery, XfsRegistryDiscovery>();
builder.Services.AddSingleton<IWindowsServiceController, ScmServiceController>();
builder.Services.AddSingleton<IProcessRunner, SafeProcessRunner>();
builder.Services.AddSingleton<IWindowsEventSource, WindowsEventLogSource>();
builder.Services.AddSingleton<ISoftwareInventory, RegistrySoftwareInventory>();
builder.Services.AddSingleton<IPatchInventory, WmiPatchInventory>();
builder.Services.AddSingleton<INetworkProbe, TcpNetworkProbe>();
builder.Services.AddSingleton<IAtmActivityProbe, ProBaseActivityProbe>();
builder.Services.AddSingleton<ILogBundleUploader, SanitizingLogBundleUploader>();
builder.Services.AddSingleton<IAgentTokenProvider, MtlsTokenProvider>();

// ---- OpenTelemetry (off by default on ATMs; enable per estate for pilot/diagnostics) ----
if (builder.Configuration.GetValue<bool>("Otel:Enabled"))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("atmnexus-agent", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString())
            .AddAttributes([new("atm.code", builder.Configuration["Agent:AtmCode"] ?? "unknown")]))
        .WithMetrics(m => m.AddMeter("AtmNexus.Xfs").AddRuntimeInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter())
        .WithTracing(t => t.AddSource("AtmNexus.Xfs").AddHttpClientInstrumentation().AddOtlpExporter());
}

await builder.Build().RunAsync();
