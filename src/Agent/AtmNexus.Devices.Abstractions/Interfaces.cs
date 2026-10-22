namespace AtmNexus.Devices;

/// <summary>Root of every device. Implementations are vendor-neutral; vendor/SP specifics live behind adapters.</summary>
public interface IATMDevice : IAsyncDisposable
{
    string LogicalName { get; }
    DeviceClass Class { get; }
    string AdapterName { get; }

    Task OpenAsync(CancellationToken ct);
    Task CloseAsync(CancellationToken ct);
    Task<DeviceStatus> GetStatusAsync(CancellationToken ct);
    Task ResetAsync(CancellationToken ct);

    /// <summary>Raised by push-capable adapters (XFS4IoT, simulator). XFS 3.x events flow through XfsEventListener instead.</summary>
    event EventHandler<DeviceEvent>? EventRaised;
}

public interface ICardReader : IATMDevice
{
    Task<CardReaderStatus> GetCardReaderStatusAsync(CancellationToken ct);
    Task<CardReadResult> ReadCardAsync(CardReadOptions options, CancellationToken ct);
    Task EjectAsync(TimeSpan timeout, CancellationToken ct);
    Task RetainAsync(CancellationToken ct);
}

public interface ICashDispenser : IATMDevice
{
    Task<IReadOnlyList<CashUnit>> GetCashUnitsAsync(CancellationToken ct);
    Task<CashSummary> GetCashSummaryAsync(CancellationToken ct);
    Task<DispenseResult> DispenseAsync(DispenseRequest request, CancellationToken ct);
    Task PresentAsync(CancellationToken ct);
    Task RejectAsync(CancellationToken ct);
    Task RetractAsync(CancellationToken ct);
}

public interface ICashRecycler : ICashDispenser
{
    Task StartCashInAsync(CancellationToken ct);
    Task<CashInResult> CashInAsync(CancellationToken ct);
    Task<CashInResult> EndCashInAsync(CancellationToken ct);
    Task RollbackCashInAsync(CancellationToken ct);
}

public interface IDepository : IATMDevice
{
    Task<IReadOnlyDictionary<string, string>> GetDepositStatusAsync(CancellationToken ct);
}

/// <summary>Monitoring only. The platform never handles PIN entry, clear keys or key loading.</summary>
public interface IPinPad : IATMDevice
{
    Task<EppInfo> GetEppInfoAsync(CancellationToken ct);
}

public interface IPrinter : IATMDevice
{
    Task<PrinterStatus> GetPrinterStatusAsync(CancellationToken ct);
}

public interface ISensor : IATMDevice
{
    Task<IReadOnlyList<SensorReading>> GetSensorsAsync(CancellationToken ct);
}

// Marker contracts for classes where only status/health is required in Phase 1.
public interface ICamera : IATMDevice { }
public interface IBarcodeReader : IATMDevice { }
public interface IPassportReader : IATMDevice { }
public interface INfcReader : IATMDevice { }
public interface IBiometricDevice : IATMDevice { }

/// <summary>Plugin contract: one factory per adapter technology (xfs3, xfs4iot, simulator, vendor-native).</summary>
public interface IDeviceAdapterFactory
{
    string AdapterName { get; }
    bool CanCreate(DeviceDescriptor descriptor);
    IATMDevice Create(DeviceDescriptor descriptor, IServiceProvider services);
}

public interface IDeviceManager : IAsyncDisposable
{
    IReadOnlyCollection<IATMDevice> Devices { get; }
    Task StartAsync(CancellationToken ct);
    T? Get<T>(string logicalName) where T : class, IATMDevice;
    IEnumerable<T> GetAll<T>() where T : class, IATMDevice;
    Task<IReadOnlyList<DeviceStatus>> PollAllAsync(CancellationToken ct);
}
