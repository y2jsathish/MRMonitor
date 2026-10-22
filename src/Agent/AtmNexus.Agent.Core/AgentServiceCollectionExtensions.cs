using AtmNexus.Agent.Cache;
using AtmNexus.Agent.Commands;
using AtmNexus.Agent.Devices;
using AtmNexus.Agent.Ej;
using AtmNexus.Agent.Health;
using AtmNexus.Agent.ProBase;
using AtmNexus.Agent.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AtmNexus.Agent;

public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the platform-neutral agent pipeline. The HOST must additionally register:
    /// <see cref="IXfsApi"/>, IWindowsServiceController, IProcessRunner, IWindowsEventSource, ISoftwareInventory, IPatchInventory,
    /// INetworkProbe, IAtmActivityProbe, ILogBundleUploader, IAgentTokenProvider.
    /// Production host = AtmNexus.Agent (Win32 implementations); simulator host = AtmNexus.Simulator (mock implementations).
    /// </summary>
    public static IServiceCollection AddNexusAgentCore(this IServiceCollection s, IConfiguration cfg,
        Action<IHttpClientBuilder>? configureGatewayClient = null)
    {
        // ---- options ----
        s.Configure<AgentOptions>(cfg.GetSection(AgentOptions.Section));
        s.Configure<DeviceManagerOptions>(cfg.GetSection("DeviceManager"));
        s.Configure<XfsApiOptions>(cfg.GetSection("Xfs:Api"));
        s.Configure<XfsServiceManagerOptions>(cfg.GetSection("Xfs:Manager"));
        s.Configure<HealthScoreOptions>(cfg.GetSection("Health"));
        s.Configure<AlarmOptions>(cfg.GetSection("Alarms"));
        s.Configure<CommandPolicy>(cfg.GetSection(CommandPolicy.Section));
        s.Configure<EjOptions>(cfg.GetSection(EjOptions.Section));
        s.Configure<ProBaseOptions>(cfg.GetSection(ProBaseOptions.Section));

        // ---- core state ----
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton<AgentStateStore>();
        s.AddSingleton<IDeviceEventBus, ChannelDeviceEventBus>();
        s.AddSingleton<IUploadSignal, UploadSignal>();
        s.AddSingleton(sp => new SqliteAgentCache(sp.GetRequiredService<IOptions<AgentOptions>>().Value.CachePath, sp.GetRequiredService<TimeProvider>()));
        s.AddSingleton<IOutbox>(sp => sp.GetRequiredService<SqliteAgentCache>());
        s.AddSingleton<ICheckpointStore>(sp => sp.GetRequiredService<SqliteAgentCache>());
        s.AddSingleton<INonceStore>(sp => sp.GetRequiredService<SqliteAgentCache>());
        s.AddSingleton<AlarmPipeline>();
        s.AddSingleton<StatusReconciler>();
        s.AddSingleton(sp => new HealthScoreEngine(sp.GetRequiredService<IOptions<HealthScoreOptions>>().Value));

        // ---- devices / XFS ----
        s.AddSingleton<IXfsServiceManager, XfsServiceManager>();
        s.AddSingleton(new XfsEventProcessor());
        s.AddSingleton<IDeviceAdapterFactory, Xfs3AdapterFactory>();
        s.AddSingleton<IDeviceAdapterFactory, Xfs4IotAdapterFactory>();
        s.AddSingleton<IDeviceManager, DeviceManager>();

        // ---- workers ----
        s.AddHostedService<XfsEventListener>();
        s.AddHostedService<DevicePollerService>();
        s.AddHostedService<DeviceHealthService>();
        s.AddHostedService<AlarmProcessor>();
        s.AddHostedService<ServiceMonitorWorker>();
        s.AddHostedService<WindowsEventWorker>();
        s.AddHostedService<InventoryWorker>();
        s.AddHostedService<TelemetryUploader>();

        // ---- remote commands ----
        s.AddSingleton<IRemoteCommandHandler, RestartWindowsServiceHandler>();
        s.AddSingleton<IRemoteCommandHandler, RestartApplicationHandler>();
        s.AddSingleton<IRemoteCommandHandler, RestartXfsHandler>();
        s.AddSingleton<IRemoteCommandHandler, RestartDeviceHandler>();
        s.AddSingleton<IRemoteCommandHandler, CollectLogsHandler>();
        s.AddSingleton<IRemoteCommandHandler, CollectEjHandler>();
        s.AddSingleton<IRemoteCommandHandler, RunDiagnosticsHandler>();
        s.AddSingleton<IRemoteCommandHandler, RebootHandler>();
        s.AddSingleton<IRemoteCommandHandler, ShutdownHandler>();
        s.AddSingleton<RemoteCommandDispatcher>();
        s.AddHostedService<RemoteCommandWorker>();

        // ---- electronic journal ----
        s.AddSingleton<IEJProvider, NcrEjProvider>();
        s.AddSingleton<IEJProvider, DieboldNixdorfEjProvider>();
        s.AddSingleton<IEJProvider, KalEjProvider>();
        s.AddSingleton<IEJProvider, GenericEjProvider>();      // keep last: supports everything
        s.AddSingleton<IEjSink, GatewayEjSink>();
        s.AddSingleton<EJStorageService>();
        s.AddSingleton<EJCollector>();
        s.AddHostedService(sp => sp.GetRequiredService<EJCollector>());
        s.AddSingleton<IEjCollectionTrigger>(sp => sp.GetRequiredService<EJCollector>());

        // ---- ProBase ----
        s.AddSingleton<ProBaseLogParser>();
        s.AddSingleton<ProBaseHealthMonitor>();
        s.AddHostedService(sp => sp.GetRequiredService<ProBaseHealthMonitor>());
        s.AddSingleton<ProBaseEventPublisher>();
        s.AddHostedService<ProBaseFileWatcher>();

        // ---- gateway HTTP client ----
        s.AddTransient<BearerTokenHandler>();
        var gw = s.AddHttpClient(TelemetryUploader.ClientName, (sp, c) =>
            {
                c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AgentOptions>>().Value.GatewayBaseUrl);
                c.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<BearerTokenHandler>();
        configureGatewayClient?.Invoke(gw);
        return s;
    }
}
