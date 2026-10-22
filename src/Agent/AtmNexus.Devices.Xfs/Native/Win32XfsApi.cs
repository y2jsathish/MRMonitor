using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;

namespace AtmNexus.Devices.Xfs.Native;

/// <summary>Production <see cref="IXfsApi"/> bound to msxfs.dll.</summary>
public sealed class Win32XfsApi : IXfsApi
{
    private readonly XfsApiOptions _opt;
    private readonly ILogger<Win32XfsApi> _log;
    private readonly ConcurrentDictionary<ushort, DeviceClass> _classes = new();
    private XfsMessagePump? _pump;
    private bool _started;

    public event Action<XfsApiEvent>? EventReceived;

    public Win32XfsApi(IOptions<XfsApiOptions> opt, ILogger<Win32XfsApi> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    public void StartUp()
    {
        if (_started) return;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("msxfs.dll is only available on Windows ATMs. Use the simulator adapter elsewhere.");

        _pump = new XfsMessagePump(OnMessage, _log);
        _pump.Start();

        uint versions = ((uint)_opt.HighVersion << 16) | _opt.LowVersion;
        int hr = NativeMethods.WFSStartUp(versions, out var v);
        if (hr != 0) throw new XfsException("WFSStartUp", "*", hr);
        _log.LogInformation("XFS manager started: version={Version} desc={Desc}", v.Version, v.Description);
        _started = true;
    }

    public XfsOpenInfo Open(string logicalName, DeviceClass deviceClass, TimeSpan timeout)
    {
        uint versions = ((uint)_opt.HighVersion << 16) | _opt.LowVersion;
        int hr = NativeMethods.WFSOpen(logicalName, IntPtr.Zero, _opt.AppId, _opt.TraceLevel, (uint)timeout.TotalMilliseconds,
            versions, out var sv, out var spi, out var handle);
        if (hr != 0) throw new XfsException("WFSOpen", logicalName, hr);
        _classes[handle] = deviceClass;
        return new XfsOpenInfo(handle, sv.Version, spi.Version, spi.Description);
    }

    public int Close(ushort handle)
    {
        _classes.TryRemove(handle, out _);
        return NativeMethods.WFSClose(handle);
    }

    public int Register(ushort handle, XfsEventClass classes) =>
        NativeMethods.WFSRegister(handle, (uint)classes, _pump?.Handle ?? IntPtr.Zero);

    public int Deregister(ushort handle, XfsEventClass classes) =>
        NativeMethods.WFSDeregister(handle, (uint)classes, _pump?.Handle ?? IntPtr.Zero);

    public int Lock(ushort handle, TimeSpan timeout)
    {
        int hr = NativeMethods.WFSLock(handle, (uint)timeout.TotalMilliseconds, out var result);
        return Complete(hr, result, parser: null).HResult;
    }

    public int Unlock(ushort handle) => NativeMethods.WFSUnlock(handle);

    public XfsResponse GetInfo(ushort handle, uint category, object? query, TimeSpan timeout)
    {
        var allocs = new List<IntPtr>();
        try
        {
            IntPtr q = MarshalInput(query, allocs);
            int hr = NativeMethods.WFSGetInfo(handle, category, q, (uint)timeout.TotalMilliseconds, out var result);
            return Complete(hr, result, buf => ParseInfo(category, buf));
        }
        finally { Free(allocs); }
    }

    public XfsResponse Execute(ushort handle, uint command, object? data, TimeSpan timeout)
    {
        var allocs = new List<IntPtr>();
        try
        {
            IntPtr d = MarshalInput(data, allocs);
            int hr = NativeMethods.WFSExecute(handle, command, d, (uint)timeout.TotalMilliseconds, out var result);
            return Complete(hr, result, buf => ParseExecute(command, buf));
        }
        finally { Free(allocs); }
    }

    // ---- marshalling helpers ----
    private static IntPtr MarshalInput(object? input, List<IntPtr> allocs) => input switch
    {
        null => IntPtr.Zero,
        ushort us => Cen30Marshaller.AllocUShort(us, allocs),
        XfsCdmDispenseData d => Cen30Marshaller.AllocDispense(d, allocs),
        _ => throw new NotSupportedException($"No native marshaller registered for {input.GetType().Name}.")
    };

    private static void Free(List<IntPtr> allocs)
    {
        foreach (var p in allocs) Marshal.FreeHGlobal(p);
    }

    private static XfsResponse Complete(int callHr, IntPtr result, Func<IntPtr, object?>? parser)
    {
        if (result == IntPtr.Zero) return new XfsResponse(callHr, null);
        try
        {
            int hr = callHr != 0 ? callHr : Marshal.ReadInt32(result, NativeMethods.ResultOffsetHResult);
            object? data = null;
            if (hr == 0 && parser is not null)
            {
                var buf = Marshal.ReadIntPtr(result, NativeMethods.ResultOffsetBuffer);
                if (buf != IntPtr.Zero) data = parser(buf);
            }
            return new XfsResponse(hr, data);
        }
        finally { NativeMethods.WFSFreeResult(result); }
    }

    private static object? ParseInfo(uint category, IntPtr buf)
    {
        if (XfsStatusLayouts.ForCategory(category) is { } layout) return Cen30Marshaller.ReadStatus(buf, layout);
        return category == XfsIds.Cdm.InfoCashUnitInfo ? Cen30Marshaller.ReadCashUnitInfo(buf) : null;
    }

    private static object? ParseExecute(uint command, IntPtr buf) =>
        command == XfsIds.Idc.CmdReadRawData ? Cen30Marshaller.ReadCardData(buf) : null;

    // ---- event delivery (runs on the pump thread; the pump frees lParam) ----
    private bool OnMessage(uint msg, IntPtr lParam)
    {
        XfsEventKind? kind = msg switch
        {
            XfsMessagePump.WFS_EXECUTE_EVENT => XfsEventKind.Execute,
            XfsMessagePump.WFS_SERVICE_EVENT => XfsEventKind.Service,
            XfsMessagePump.WFS_USER_EVENT => XfsEventKind.User,
            XfsMessagePump.WFS_SYSTEM_EVENT => XfsEventKind.System,
            _ => null
        };
        if (kind is null || lParam == IntPtr.Zero) return false;

        ushort handle = (ushort)Marshal.ReadInt16(lParam, NativeMethods.ResultOffsetHService);
        int hr = Marshal.ReadInt32(lParam, NativeMethods.ResultOffsetHResult);
        uint eventId = (uint)Marshal.ReadInt32(lParam, NativeMethods.ResultOffsetEventOrCommand);
        IntPtr buf = Marshal.ReadIntPtr(lParam, NativeMethods.ResultOffsetBuffer);

        object? data = null;
        if (buf != IntPtr.Zero && _classes.TryGetValue(handle, out var cls))
        {
            var payloadKind = XfsEventCatalog.Default.PayloadKind(cls, kind.Value, eventId);
            data = payloadKind switch
            {
                XfsPayloadKind.UShort => new XfsUShortData((ushort)Marshal.ReadInt16(buf)),
                XfsPayloadKind.CdmCashUnit => Cen30Marshaller.ReadCashUnit(buf),
                XfsPayloadKind.SiuPort => new XfsSiuPortEventData(
                    (ushort)Marshal.ReadInt16(buf, 0), (ushort)Marshal.ReadInt16(buf, 2), (ushort)Marshal.ReadInt16(buf, 4)),
                _ => null
            };
        }

        EventReceived?.Invoke(new XfsApiEvent(kind.Value, handle, eventId, hr, data, DateTimeOffset.UtcNow));
        return true;
    }

    public void Dispose()
    {
        if (_started && OperatingSystem.IsWindows())
        {
            try { NativeMethods.WFSCleanUp(); } catch (Exception ex) { _log.LogDebug(ex, "WFSCleanUp failed"); }
        }
        _pump?.Dispose();
    }
}
