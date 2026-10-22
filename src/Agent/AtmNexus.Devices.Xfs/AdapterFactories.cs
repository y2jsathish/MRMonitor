using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AtmNexus.Devices.Xfs.Xfs4Iot;

namespace AtmNexus.Devices.Xfs;

/// <summary>Creates CEN/XFS 3.x devices (adapter "xfs3"); the simulator reuses it with MockXfsApi as the IXfsApi.</summary>
public sealed class Xfs3AdapterFactory : IDeviceAdapterFactory
{
    public string AdapterName => "xfs3";
    public bool CanCreate(DeviceDescriptor d) => d.Adapter is "xfs3" or "simulator";

    public IATMDevice Create(DeviceDescriptor d, IServiceProvider sp)
    {
        var xfs = sp.GetRequiredService<IXfsServiceManager>();
        var lf = sp.GetRequiredService<ILoggerFactory>();
        return d.Class switch
        {
            DeviceClass.CardReader or DeviceClass.NfcReader => new CardReaderService(xfs, d, lf.CreateLogger<CardReaderService>()),
            DeviceClass.CashDispenser or DeviceClass.CashRecycler => new CashDispenserService(
                new Xfs3CdmBackend(xfs, d), d, CashOptions(d), lf.CreateLogger<CashDispenserService>()),
            DeviceClass.Printer => new PrinterService(xfs, d, lf.CreateLogger<PrinterService>()),
            DeviceClass.PinPad => new PinpadService(xfs, d, lf.CreateLogger<PinpadService>()),
            DeviceClass.Sensors => new SiuService(xfs, d, sp.GetService<SiuSensorMap>() ?? SiuSensorMap.Default, lf.CreateLogger<SiuService>()),
            _ => throw new NotSupportedException($"No XFS3 adapter for {d.Class}; add an IDeviceAdapterFactory plugin.")
        };
    }

    internal static CashDispenserOptions CashOptions(DeviceDescriptor d) => new()
    {
        AllowDispense = d.Setting("AllowDispense", "false") == "true",
        MaxDispenseAmount = long.TryParse(d.Setting("MaxDispenseAmount", "100"), out var m) ? m : 100
    };
}

/// <summary>Creates XFS4IoT devices (adapter "xfs4iot"): endpoint comes from DeviceDescriptor.Endpoint.</summary>
public sealed class Xfs4IotAdapterFactory : IDeviceAdapterFactory
{
    public string AdapterName => "xfs4iot";
    public bool CanCreate(DeviceDescriptor d) => d.Adapter == "xfs4iot" && d.Class is DeviceClass.CashDispenser or DeviceClass.CashRecycler;

    public IATMDevice Create(DeviceDescriptor d, IServiceProvider sp)
    {
        var lf = sp.GetRequiredService<ILoggerFactory>();
        var client = new Xfs4IotClient(new Uri(d.Endpoint ?? throw new InvalidOperationException($"Endpoint missing for {d.LogicalName}")),
            lf.CreateLogger<Xfs4IotClient>());
        var backend = new Xfs4IotCdmBackend(client, d, lf.CreateLogger<Xfs4IotCdmBackend>());
        return new CashDispenserService(backend, d, Xfs3AdapterFactory.CashOptions(d), lf.CreateLogger<CashDispenserService>());
    }
}
