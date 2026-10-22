namespace AtmNexus.Devices.Xfs;

/// <summary>Common open/status/reset behaviour for CEN/XFS 3.x service providers.</summary>
public abstract class Xfs3Device(IXfsServiceManager xfs, DeviceDescriptor descriptor, ILogger log) : IATMDevice
{
    protected IXfsServiceManager Xfs { get; } = xfs;
    protected ILogger Log { get; } = log;
    protected ushort Handle { get; private set; }
    protected bool IsOpen { get; private set; }

    public string LogicalName => descriptor.LogicalName;
    public DeviceClass Class => descriptor.Class;
    public string AdapterName => "xfs3";
    protected DeviceDescriptor Descriptor => descriptor;

    // XFS 3.x events are delivered centrally through XfsEventListener -> XfsEventProcessor.
    public event EventHandler<DeviceEvent>? EventRaised { add { } remove { } }

    protected abstract uint StatusCategory { get; }
    protected abstract uint ResetCommand { get; }
    protected abstract DeviceStatus MapStatus(XfsStatusData status, DateTimeOffset at);

    public virtual async Task OpenAsync(CancellationToken ct)
    {
        if (IsOpen) return;
        Handle = await Xfs.OpenAsync(LogicalName, Class, ct).ConfigureAwait(false);
        await Xfs.RegisterAsync(Handle, XfsEventClass.All, ct).ConfigureAwait(false);
        IsOpen = true;
    }

    public virtual async Task CloseAsync(CancellationToken ct)
    {
        if (!IsOpen) return;
        IsOpen = false;
        await Xfs.CloseAsync(Handle, ct).ConfigureAwait(false);
    }

    protected async Task<XfsStatusData> ReadStatusAsync(CancellationToken ct)
    {
        var resp = await Xfs.GetInfoAsync(Handle, StatusCategory, null, ct).ConfigureAwait(false);
        resp.ThrowIfFailed("WFSGetInfo(STATUS)", LogicalName);
        return resp.DataAs<XfsStatusData>() ?? throw new XfsException("WFSGetInfo(STATUS)", LogicalName, 0, "empty status buffer");
    }

    public virtual async Task<DeviceStatus> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            return MapStatus(await ReadStatusAsync(ct).ConfigureAwait(false), DateTimeOffset.UtcNow);
        }
        catch (XfsException ex)
        {
            Log.LogWarning(ex, "Status read failed for {LogicalName}", LogicalName);
            return new DeviceStatus(LogicalName, Class, DeviceState.Offline,
                new Dictionary<string, string> { ["error"] = ex.XfsResult.ToString() }, DateTimeOffset.UtcNow);
        }
    }

    public virtual async Task ResetAsync(CancellationToken ct)
    {
        var r = await Xfs.ExecuteAsync(Handle, ResetCommand, null, TimeSpan.FromSeconds(90), retryable: true, ct).ConfigureAwait(false);
        r.ThrowIfFailed("WFSExecute(RESET)", LogicalName);
        Log.LogInformation("Device {LogicalName} reset", LogicalName);
    }

    public async ValueTask DisposeAsync() => await CloseAsync(CancellationToken.None).ConfigureAwait(false);
}

public sealed class CardReaderService(IXfsServiceManager xfs, DeviceDescriptor descriptor, ILogger<CardReaderService> log)
    : Xfs3Device(xfs, descriptor, log), ICardReader
{
    protected override uint StatusCategory => XfsIds.Idc.InfoStatus;
    protected override uint ResetCommand => XfsIds.Idc.CmdReset;

    protected override DeviceStatus MapStatus(XfsStatusData s, DateTimeOffset at)
    {
        var media = XfsValues.ToCardMedia(s.Field("fwMedia"));
        var bin = XfsValues.ToRetainBin(s.Field("fwRetainBin"));
        return new DeviceStatus(LogicalName, Class, XfsValues.ToDeviceState(s.Field("fwDevice")),
            new Dictionary<string, string>
            {
                [DeviceAttributes.Media] = media.ToString(),
                [DeviceAttributes.RetainBin] = bin.ToString(),
                [DeviceAttributes.RetainedCards] = s.Field("usCards", 0).ToString()
            }, at);
    }

    public async Task<CardReaderStatus> GetCardReaderStatusAsync(CancellationToken ct)
    {
        var s = await ReadStatusAsync(ct).ConfigureAwait(false);
        return new CardReaderStatus(XfsValues.ToDeviceState(s.Field("fwDevice")), XfsValues.ToCardMedia(s.Field("fwMedia")),
            XfsValues.ToRetainBin(s.Field("fwRetainBin")), s.Field("usCards", 0));
    }

    public async Task<CardReadResult> ReadCardAsync(CardReadOptions options, CancellationToken ct)
    {
        ushort flags = 0;
        if (options.Track1) flags |= XfsIds.Idc.Track1;
        if (options.Track2) flags |= XfsIds.Idc.Track2;
        if (options.Track3) flags |= XfsIds.Idc.Track3;
        if (options.Chip) flags |= XfsIds.Idc.Chip;

        // Reading a card is NOT idempotent (the cardholder may have removed it): never retried.
        var resp = await Xfs.ExecuteAsync(Handle, XfsIds.Idc.CmdReadRawData, flags, options.Timeout, retryable: false, ct)
            .ConfigureAwait(false);
        resp.ThrowIfFailed("WFSExecute(READ_RAW_DATA)", LogicalName);

        var items = resp.DataAs<XfsCardDataResult>()?.Items ?? [];
        var tracks = new List<TrackReadStatus>();
        string? masked = null, svc = null;
        bool chipPresent = false, emv = false, chipSvc = false;
        Dictionary<int, SensitiveBytes>? raw = null;

        try
        {
            foreach (var it in items)
            {
                var status = (TrackDataStatus)Math.Min((int)it.Status, (int)TrackDataStatus.SourceMissing);
                if (it.Source == XfsIds.Idc.Chip)
                {
                    chipPresent = it.Status == 0 && it.Data.Length > 0;
                    emv = chipPresent && AtrParser.TryParse(it.Data, out var atr) && atr!.EmvCompatible;
                    tracks.Add(new TrackReadStatus(0, status, it.Data.Length));
                    continue;
                }
                int track = it.Source switch { XfsIds.Idc.Track1 => 1, XfsIds.Idc.Track2 => 2, XfsIds.Idc.Track3 => 3, _ => -1 };
                if (track < 0) continue;
                tracks.Add(new TrackReadStatus(track, status, it.Data.Length));

                if (track == 2 && it.Status == 0 && Track2Parser.TryParse(it.Data, out var t2))
                {
                    masked = t2!.MaskedPan;
                    svc = t2.ServiceCode;
                    chipSvc = t2.ChipCardPerServiceCode;
                    if (options.ReturnRawTracks && IsRawAllowed(masked))
                    {
                        raw ??= new();
                        raw[2] = new SensitiveBytes((byte[])it.Data.Clone());
                    }
                }
            }
        }
        finally
        {
            foreach (var it in items) Array.Clear(it.Data);   // never leave track data on the heap
        }

        // Chip card (service code 2xx/6xx) presented but chip unreadable => possible magstripe fallback / skimming indicator.
        bool fallback = options.Chip && chipSvc && !chipPresent;
        if (fallback) Log.LogWarning("Technical fallback suspected on {LogicalName}", LogicalName);
        return new CardReadResult(items.Count > 0, chipPresent, emv, fallback, svc, masked, tracks, raw);
    }

    private bool IsRawAllowed(string maskedPan)
    {
        var allowRaw = Descriptor.Setting("AllowRawTracks", "false") == "true";
        var bins = Descriptor.Setting("TestBins").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowRaw && bins.Contains(maskedPan[..6]);
    }

    public async Task EjectAsync(TimeSpan timeout, CancellationToken ct)
    {
        var r = await Xfs.ExecuteAsync(Handle, XfsIds.Idc.CmdEjectCard, null, timeout, retryable: false, ct).ConfigureAwait(false);
        r.ThrowIfFailed("WFSExecute(EJECT_CARD)", LogicalName);
    }

    public async Task RetainAsync(CancellationToken ct)
    {
        var r = await Xfs.ExecuteAsync(Handle, XfsIds.Idc.CmdRetainCard, null, TimeSpan.FromSeconds(30), retryable: false, ct).ConfigureAwait(false);
        r.ThrowIfFailed("WFSExecute(RETAIN_CARD)", LogicalName);
    }
}

public sealed class PrinterService(IXfsServiceManager xfs, DeviceDescriptor descriptor, ILogger<PrinterService> log)
    : Xfs3Device(xfs, descriptor, log), IPrinter
{
    protected override uint StatusCategory => XfsIds.Ptr.InfoStatus;
    protected override uint ResetCommand => XfsIds.Ptr.CmdReset;

    private static (SupplyLevel level, bool jammed) WorstPaper(XfsStatusData s)
    {
        var sources = s.Array("fwPaper");
        bool jammed = sources.Any(v => v == 5);
        var levels = sources.Select(XfsValues.ToSupply).Where(l => l is not (SupplyLevel.NotSupported or SupplyLevel.Unknown)).ToList();
        if (levels.Count == 0) return (SupplyLevel.Unknown, jammed);
        // Worst-of: Out < Low < Full  (a receipt printer is useless when its only roll is out)
        var worst = levels.Contains(SupplyLevel.Out) ? SupplyLevel.Out : levels.Contains(SupplyLevel.Low) ? SupplyLevel.Low : SupplyLevel.Full;
        return (worst, jammed);
    }

    protected override DeviceStatus MapStatus(XfsStatusData s, DateTimeOffset at)
    {
        var (paper, jammed) = WorstPaper(s);
        return new DeviceStatus(LogicalName, Class, XfsValues.ToDeviceState(s.Field("fwDevice")),
            new Dictionary<string, string>
            {
                [DeviceAttributes.Paper] = jammed ? "Jammed" : paper.ToString(),
                [DeviceAttributes.Media] = XfsValues.ToPrinterMedia(s.Field("fwMedia")).ToString(),
                [DeviceAttributes.Toner] = XfsValues.ToSupply(s.Field("fwToner")).ToString()
            }, at);
    }

    public async Task<PrinterStatus> GetPrinterStatusAsync(CancellationToken ct)
    {
        var s = await ReadStatusAsync(ct).ConfigureAwait(false);
        var (paper, jammed) = WorstPaper(s);
        return new PrinterStatus(XfsValues.ToDeviceState(s.Field("fwDevice")), XfsValues.ToPrinterMedia(s.Field("fwMedia")),
            paper, XfsValues.ToSupply(s.Field("fwToner")), jammed);
    }
}

public sealed class PinpadService(IXfsServiceManager xfs, DeviceDescriptor descriptor, ILogger<PinpadService> log)
    : Xfs3Device(xfs, descriptor, log), IPinPad
{
    protected override uint StatusCategory => XfsIds.Pin.InfoStatus;
    protected override uint ResetCommand => XfsIds.Pin.CmdReset;

    protected override DeviceStatus MapStatus(XfsStatusData s, DateTimeOffset at) =>
        new(LogicalName, Class, XfsValues.ToDeviceState(s.Field("fwDevice")),
            new Dictionary<string, string> { [DeviceAttributes.Encryptor] = XfsValues.ToEncryptor(s.Field("fwEncStat")).ToString() }, at);

    public async Task<EppInfo> GetEppInfoAsync(CancellationToken ct)
    {
        var s = await ReadStatusAsync(ct).ConfigureAwait(false);
        var enc = XfsValues.ToEncryptor(s.Field("fwEncStat"));
        return new EppInfo(XfsValues.ToDeviceState(s.Field("fwDevice")), enc, Descriptor.Setting("Serial", "") is { Length: > 0 } sn ? sn : null,
            Descriptor.Setting("Firmware", "") is { Length: > 0 } fw ? fw : null, TamperSuspected: enc == EncryptorState.Tampered);
    }
}

/// <summary>Sensors & Indicators Unit: doors, tamper, seismic, heat. Sensor layout comes from <see cref="SiuSensorMap"/>.</summary>
public sealed class SiuService(IXfsServiceManager xfs, DeviceDescriptor descriptor, SiuSensorMap map, ILogger<SiuService> log)
    : Xfs3Device(xfs, descriptor, log), ISensor
{
    protected override uint StatusCategory => XfsIds.Siu.InfoStatus;
    protected override uint ResetCommand => XfsIds.Siu.CmdReset;

    public override async Task OpenAsync(CancellationToken ct)
    {
        await base.OpenAsync(ct).ConfigureAwait(false);
        // WFS_CMD_SIU_ENABLE_EVENTS: ask the SP to emit WFS_SRVE_SIU_PORT_STATUS for all ports.
        var r = await Xfs.ExecuteAsync(Handle, XfsIds.Siu.CmdEnableEvents, null, TimeSpan.FromSeconds(10), retryable: true, ct).ConfigureAwait(false);
        if (!r.Success) Log.LogWarning("SIU ENABLE_EVENTS on {LogicalName} returned {HResult}; relying on polling", LogicalName, r.HResult);
    }

    private IReadOnlyList<SensorReading> Read(XfsStatusData s)
    {
        var list = new List<SensorReading>();
        foreach (var e in map.All)
        {
            var arr = s.Array(e.PortType == 2 ? "fwDoors" : "fwSensors");
            if (e.PortIndex >= arr.Length) continue;
            var raw = arr[e.PortIndex];
            list.Add(new SensorReading(e.Kind, e.Name, (raw & e.ActiveMask) != 0 ? SensorState.Active : SensorState.Inactive, raw));
        }
        return list;
    }

    protected override DeviceStatus MapStatus(XfsStatusData s, DateTimeOffset at)
    {
        var attrs = new Dictionary<string, string>();
        foreach (var r in Read(s))
        {
            bool on = r.State == SensorState.Active;
            switch (r.Kind)
            {
                case SensorKind.SafeDoor: attrs[DeviceAttributes.SafeDoor] = on ? DeviceAttributes.Open : DeviceAttributes.Closed; break;
                case SensorKind.CabinetDoor or SensorKind.VandalShield: attrs[DeviceAttributes.CabinetDoor] = on ? DeviceAttributes.Open : DeviceAttributes.Closed; break;
                case SensorKind.Tamper or SensorKind.InternalTamper:
                    if (on || !attrs.ContainsKey(DeviceAttributes.Tamper)) attrs[DeviceAttributes.Tamper] = on ? DeviceAttributes.Active : DeviceAttributes.Inactive; break;
                case SensorKind.Vibration: attrs[DeviceAttributes.Vibration] = on ? DeviceAttributes.Active : DeviceAttributes.Inactive; break;
                case SensorKind.Temperature: attrs[DeviceAttributes.Heat] = on ? DeviceAttributes.Active : DeviceAttributes.Inactive; break;
            }
        }
        return new DeviceStatus(LogicalName, Class, XfsValues.ToDeviceState(s.Field("fwDevice")), attrs, at);
    }

    public async Task<IReadOnlyList<SensorReading>> GetSensorsAsync(CancellationToken ct) =>
        Read(await ReadStatusAsync(ct).ConfigureAwait(false));
}
