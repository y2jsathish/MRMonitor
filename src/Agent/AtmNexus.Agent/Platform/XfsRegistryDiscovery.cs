using AtmNexus.Agent.Devices;
using Microsoft.Win32;

namespace AtmNexus.Agent.Platform.Windows;

/// <summary>
/// Reads the CEN/XFS configuration hive: HKLM\SOFTWARE\XFS\LOGICAL_SERVICES\&lt;name&gt; (value "class" = IDC|CDM|PTR|PIN|SIU|...,
/// value "provider" = physical service provider key). Older XFS managers keep the same tree under HKEY_USERS\.DEFAULT\XFS.
/// The provider key name is used to guess the vendor (NCR/Wincor/Hyosung ...), which selects the vendor profile.
/// </summary>
public sealed class XfsRegistryDiscovery(ILogger<XfsRegistryDiscovery> log) : IDeviceDiscovery
{
    private static readonly (RegistryKey Hive, string Path)[] Roots =
    [
        (Registry.LocalMachine, @"SOFTWARE\XFS\LOGICAL_SERVICES"),
        (Registry.Users, @".DEFAULT\XFS\LOGICAL_SERVICES")
    ];

    public IReadOnlyList<DeviceDescriptor> Discover()
    {
        var list = new List<DeviceDescriptor>();
        foreach (var (hive, path) in Roots)
        {
            using var root = hive.OpenSubKey(path);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                var cls = (k?.GetValue("class") as string)?.Trim().ToUpperInvariant();
                var provider = k?.GetValue("provider") as string ?? "";
                var dc = MapClass(cls);
                if (dc is null)
                {
                    log.LogDebug("Skipping logical service {Name} with unsupported class {Class}", name, cls);
                    continue;
                }
                if (list.Any(d => d.LogicalName.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new DeviceDescriptor { LogicalName = name, Class = dc.Value, Vendor = GuessVendor(provider), Adapter = "xfs3" });
            }
        }
        return list;
    }

    internal static DeviceClass? MapClass(string? c) => c switch
    {
        "IDC" => DeviceClass.CardReader,
        "CDM" => DeviceClass.CashDispenser,
        "CIM" => DeviceClass.Depository,
        "DEP" => DeviceClass.Depository,
        "PTR" => DeviceClass.Printer,
        "PIN" => DeviceClass.PinPad,
        "SIU" => DeviceClass.Sensors,
        "CAM" => DeviceClass.Camera,
        "BCR" => DeviceClass.BarcodeReader,
        _ => null
    };

    internal static string GuessVendor(string provider)
    {
        var p = provider.ToLowerInvariant();
        if (p.Contains("ncr") || p.Contains("aptra")) return "NCR";
        if (p.Contains("wincor") || p.Contains("nixdorf") || p.Contains("dn") || p.Contains("protopas")) return "DieboldNixdorf";
        if (p.Contains("hyosung") || p.Contains("nautilus") || p.Contains("mx")) return "Hyosung";
        if (p.Contains("grg")) return "GRG";
        if (p.Contains("hitachi") || p.Contains("omron")) return "Hitachi";
        if (p.Contains("kal")) return "KAL";
        return "Generic";
    }
}
