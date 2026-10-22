namespace AtmNexus.Devices.Xfs;

/// <summary>
/// Bridge implementor: transport-specific CDM access (CEN/XFS 3.x via msxfs.dll, XFS4IoT via WebSocket).
/// <see cref="CashDispenserService"/> (the abstraction) contains all business rules once.
/// </summary>
public interface ICdmBackend : IAsyncDisposable
{
    string AdapterName { get; }
    event EventHandler<DeviceEvent>? EventRaised;
    Task OpenAsync(CancellationToken ct);
    Task CloseAsync(CancellationToken ct);
    Task<DeviceStatus> GetStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<CashUnit>> GetCashUnitsAsync(CancellationToken ct);
    /// <summary>Executes the dispense. Implementations MUST NOT retry.</summary>
    Task DispenseAsync(DispenseRequest request, CancellationToken ct);
    Task PresentAsync(CancellationToken ct);
    Task RejectAsync(CancellationToken ct);
    Task RetractAsync(CancellationToken ct);
    Task ResetAsync(CancellationToken ct);
}

public sealed class CashDispenserOptions
{
    /// <summary>
    /// Dispensing from the monitoring platform is a diagnostic capability only (test-note dispense after maintenance).
    /// Disabled by default; enable per ATM via signed config and never in production customer mode.
    /// </summary>
    public bool AllowDispense { get; set; }
    public long MaxDispenseAmount { get; set; } = 100;
    public TimeSpan DispenseTimeout { get; set; } = TimeSpan.FromSeconds(60);
}

public sealed class CashDispenserService : ICashDispenser
{
    private readonly ICdmBackend _backend;
    private readonly DeviceDescriptor _descriptor;
    private readonly CashDispenserOptions _opt;
    private readonly ILogger<CashDispenserService> _log;

    public CashDispenserService(ICdmBackend backend, DeviceDescriptor descriptor, CashDispenserOptions opt,
        ILogger<CashDispenserService> log)
    {
        _backend = backend;
        _descriptor = descriptor;
        _opt = opt;
        _log = log;
        _backend.EventRaised += (s, e) => EventRaised?.Invoke(this, e);
    }

    public string LogicalName => _descriptor.LogicalName;
    public DeviceClass Class => _descriptor.Class;
    public string AdapterName => _backend.AdapterName;
    public event EventHandler<DeviceEvent>? EventRaised;

    public Task OpenAsync(CancellationToken ct) => _backend.OpenAsync(ct);
    public Task CloseAsync(CancellationToken ct) => _backend.CloseAsync(ct);
    public Task<DeviceStatus> GetStatusAsync(CancellationToken ct) => _backend.GetStatusAsync(ct);
    public Task<IReadOnlyList<CashUnit>> GetCashUnitsAsync(CancellationToken ct) => _backend.GetCashUnitsAsync(ct);

    public async Task<CashSummary> GetCashSummaryAsync(CancellationToken ct) =>
        CashCalculator.Summarize(await GetCashUnitsAsync(ct).ConfigureAwait(false));

    /// <summary>CalculateRemainingCash(): dispensable value for a currency, excluding empty/faulty/locked units.</summary>
    public async Task<long> CalculateRemainingCashAsync(string currency, CancellationToken ct) =>
        CashCalculator.RemainingCash(await GetCashUnitsAsync(ct).ConfigureAwait(false), currency);

    public async Task<DispenseResult> DispenseAsync(DispenseRequest request, CancellationToken ct)
    {
        if (!_opt.AllowDispense)
            return new DispenseResult(false, 0, [], "Dispense is disabled on this ATM (diagnostic mode required).");
        if (request.Amount <= 0 || request.Amount > _opt.MaxDispenseAmount)
            return new DispenseResult(false, 0, [], $"Amount must be between 1 and {_opt.MaxDispenseAmount}.");
        if (request.Currency is not { Length: 3 })
            return new DispenseResult(false, 0, [], "Currency must be an ISO-4217 alpha-3 code.");

        var before = await _backend.GetCashUnitsAsync(ct).ConfigureAwait(false);
        if (!CashCalculator.CanDispense(before, request.Currency, request.Amount))
            return new DispenseResult(false, 0, [], "Requested amount cannot be composed from the notes currently available.");

        try
        {
            _log.LogInformation("Dispense requested on {LogicalName}: {Currency} {Amount}", LogicalName, request.Currency, request.Amount);
            await _backend.DispenseAsync(request, ct).ConfigureAwait(false);
        }
        catch (XfsException ex)
        {
            // No retry: the ATM may have physically moved notes. Reconcile from counters and let an operator decide.
            _log.LogError(ex, "Dispense failed on {LogicalName}; reconciling counters", LogicalName);
            var failed = await Reconcile(before, request.Currency, ct).ConfigureAwait(false);
            return failed with { Success = false, Error = ex.Message };
        }

        return await Reconcile(before, request.Currency, ct).ConfigureAwait(false);
    }

    /// <summary>Notes dispensed = counter delta across the operation (robust against SPs that omit the denomination result).</summary>
    private async Task<DispenseResult> Reconcile(IReadOnlyList<CashUnit> before, string currency, CancellationToken ct)
    {
        var after = await _backend.GetCashUnitsAsync(ct).ConfigureAwait(false);
        var notes = new List<DispensedNote>();
        foreach (var b in before.Where(u => u.Currency == currency))
        {
            var a = after.FirstOrDefault(u => u.Number == b.Number);
            if (a is null) continue;
            int delta = b.Count - a.Count;
            if (delta > 0) notes.Add(new DispensedNote(b.Denomination, delta));
        }
        long amount = notes.Sum(n => (long)n.Denomination * n.Count);
        return new DispenseResult(true, amount, notes, null);
    }

    public Task PresentAsync(CancellationToken ct) => Log("present", _backend.PresentAsync(ct));
    public Task RejectAsync(CancellationToken ct) => Log("reject", _backend.RejectAsync(ct));
    public Task RetractAsync(CancellationToken ct) => Log("retract", _backend.RetractAsync(ct));
    public Task ResetAsync(CancellationToken ct) => Log("reset", _backend.ResetAsync(ct));

    private async Task Log(string op, Task t)
    {
        await t.ConfigureAwait(false);
        _log.LogInformation("CDM {Op} completed on {LogicalName}", op, LogicalName);
    }

    public ValueTask DisposeAsync() => _backend.DisposeAsync();
}

/// <summary>CEN/XFS 3.x implementor of the CDM bridge.</summary>
public sealed class Xfs3CdmBackend(IXfsServiceManager xfs, DeviceDescriptor descriptor) : ICdmBackend
{
    private ushort _handle;
    private bool _open;

    public string AdapterName => "xfs3";
    public event EventHandler<DeviceEvent>? EventRaised { add { } remove { } }

    public async Task OpenAsync(CancellationToken ct)
    {
        if (_open) return;
        _handle = await xfs.OpenAsync(descriptor.LogicalName, descriptor.Class, ct).ConfigureAwait(false);
        await xfs.RegisterAsync(_handle, XfsEventClass.All, ct).ConfigureAwait(false);
        _open = true;
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        if (!_open) return;
        _open = false;
        await xfs.CloseAsync(_handle, ct).ConfigureAwait(false);
    }

    public async Task<DeviceStatus> GetStatusAsync(CancellationToken ct)
    {
        var resp = await xfs.GetInfoAsync(_handle, XfsIds.Cdm.InfoStatus, null, ct).ConfigureAwait(false);
        if (!resp.Success || resp.DataAs<XfsStatusData>() is not { } s)
            return new DeviceStatus(descriptor.LogicalName, descriptor.Class, DeviceState.Offline,
                new Dictionary<string, string> { ["error"] = resp.HResult.ToString() }, DateTimeOffset.UtcNow);

        var attrs = new Dictionary<string, string>
        {
            [DeviceAttributes.SafeDoor] = s.Field("fwSafeDoor") switch { 1 => "open", 2 => "closed", 0 => "notSupported", _ => "unknown" },
            [DeviceAttributes.Dispenser] = s.Field("fwDispenser") switch { 0 => "ok", 1 => "cuState", 2 => "cuStop", _ => "unknown" }
        };
        return new DeviceStatus(descriptor.LogicalName, descriptor.Class, XfsValues.ToDeviceState(s.Field("fwDevice")), attrs, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<CashUnit>> GetCashUnitsAsync(CancellationToken ct)
    {
        var resp = await xfs.GetInfoAsync(_handle, XfsIds.Cdm.InfoCashUnitInfo, null, ct).ConfigureAwait(false);
        resp.ThrowIfFailed("WFSGetInfo(CASH_UNIT_INFO)", descriptor.LogicalName);
        var data = resp.DataAs<XfsCashUnitInfoData>() ?? new XfsCashUnitInfoData([]);
        return data.Units.Select(u => new CashUnit(
            u.Number, u.UnitId, u.Name, XfsValues.ToCashUnitType(u.Type), u.Currency, (int)u.Value,
            (int)u.InitialCount, (int)u.Count, (int)u.RejectCount, (int)u.Minimum, (int)u.Maximum,
            XfsValues.ToCashUnitStatus(u.Status), u.AppLock)).ToList();
    }

    public async Task DispenseAsync(DispenseRequest r, CancellationToken ct)
    {
        var payload = new XfsCdmDispenseData((ushort)r.TellerId, (ushort)r.MixNumber, r.Currency, (uint)r.Amount, r.Present);
        var resp = await xfs.ExecuteAsync(_handle, XfsIds.Cdm.CmdDispense, payload, TimeSpan.FromSeconds(60), retryable: false, ct).ConfigureAwait(false);
        resp.ThrowIfFailed("WFSExecute(DISPENSE)", descriptor.LogicalName);
    }

    public Task PresentAsync(CancellationToken ct) => Exec(XfsIds.Cdm.CmdPresent, "PRESENT", false, ct);
    public Task RejectAsync(CancellationToken ct) => Exec(XfsIds.Cdm.CmdReject, "REJECT", false, ct);
    public Task RetractAsync(CancellationToken ct) => Exec(XfsIds.Cdm.CmdRetract, "RETRACT", false, ct);
    public Task ResetAsync(CancellationToken ct) => Exec(XfsIds.Cdm.CmdReset, "RESET", true, ct);

    private async Task Exec(uint cmd, string name, bool retryable, CancellationToken ct)
    {
        var resp = await xfs.ExecuteAsync(_handle, cmd, null, TimeSpan.FromSeconds(60), retryable, ct).ConfigureAwait(false);
        resp.ThrowIfFailed($"WFSExecute({name})", descriptor.LogicalName);
    }

    public async ValueTask DisposeAsync() => await CloseAsync(CancellationToken.None).ConfigureAwait(false);
}
