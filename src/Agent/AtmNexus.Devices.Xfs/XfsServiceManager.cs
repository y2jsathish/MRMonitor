using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace AtmNexus.Devices.Xfs;

public enum XfsErrorClass { Success, Transient, DeviceFault, UserError, Fatal, Unknown }

public interface IXfsErrorClassifier { XfsErrorClass Classify(int hResult); }

/// <summary>
/// Table-driven so vendor profiles can map their WFS_ERR_* values (XFSAPI.H differs per SDK generation).
/// Unknown negative codes are <see cref="XfsErrorClass.Unknown"/>: GetInfo may be retried, Execute never is.
/// </summary>
public sealed class TableXfsErrorClassifier(IReadOnlyDictionary<int, XfsErrorClass>? overrides = null) : IXfsErrorClassifier
{
    public XfsErrorClass Classify(int hResult)
    {
        if (hResult == 0) return XfsErrorClass.Success;
        if (overrides is not null && overrides.TryGetValue(hResult, out var c)) return c;
        return XfsErrorClass.Unknown;
    }
}

public sealed class XfsServiceManagerOptions
{
    public int MaxInfoAttempts { get; set; } = 3;
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    public Dictionary<int, XfsErrorClass> ErrorOverrides { get; set; } = new();
}

public interface IXfsServiceManager : IDisposable
{
    event Action<XfsEvent>? EventReceived;
    Task<ushort> OpenAsync(string logicalName, DeviceClass deviceClass, CancellationToken ct);
    Task RegisterAsync(ushort handle, XfsEventClass classes, CancellationToken ct);
    Task LockAsync(ushort handle, TimeSpan timeout, CancellationToken ct);
    Task UnlockAsync(ushort handle, CancellationToken ct);
    Task<XfsResponse> GetInfoAsync(ushort handle, uint category, object? query, CancellationToken ct);
    /// <param name="retryable">Only true for idempotent commands (Reset). NEVER true for Dispense/Present/Retract.</param>
    Task<XfsResponse> ExecuteAsync(ushort handle, uint command, object? data, TimeSpan timeout, bool retryable, CancellationToken ct);
    Task CloseAsync(ushort handle, CancellationToken ct);
}

/// <summary>
/// Serialises calls per service handle (one outstanding command per SP), moves blocking WFS* calls off the
/// caller thread, adds retry for idempotent calls, structured logs, traces and metrics.
/// </summary>
public sealed class XfsServiceManager : IXfsServiceManager
{
    public static readonly ActivitySource Activity = new("AtmNexus.Xfs");
    private static readonly Meter Meter = new("AtmNexus.Xfs");
    private static readonly Counter<long> Calls = Meter.CreateCounter<long>("xfs.calls");
    private static readonly Counter<long> Errors = Meter.CreateCounter<long>("xfs.errors");
    private static readonly Histogram<double> Latency = Meter.CreateHistogram<double>("xfs.call.duration", "ms");

    private sealed class Session(string name, DeviceClass cls)
    {
        public string Name { get; } = name;
        public DeviceClass Class { get; } = cls;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly IXfsApi _api;
    private readonly IXfsErrorClassifier _classifier;
    private readonly XfsServiceManagerOptions _opt;
    private readonly XfsApiOptions _apiOpt;
    private readonly ILogger<XfsServiceManager> _log;
    private readonly ConcurrentDictionary<ushort, Session> _sessions = new();
    private readonly object _startLock = new();
    private bool _started;

    public event Action<XfsEvent>? EventReceived;

    public XfsServiceManager(IXfsApi api, IOptions<XfsServiceManagerOptions> opt, IOptions<XfsApiOptions> apiOpt,
        ILogger<XfsServiceManager> log)
    {
        _api = api;
        _opt = opt.Value;
        _apiOpt = apiOpt.Value;
        _log = log;
        _classifier = new TableXfsErrorClassifier(_opt.ErrorOverrides);
        _api.EventReceived += OnApiEvent;
    }

    private void EnsureStarted()
    {
        lock (_startLock)
        {
            if (_started) return;
            _api.StartUp();
            _started = true;
        }
    }

    private void OnApiEvent(XfsApiEvent e)
    {
        if (!_sessions.TryGetValue(e.Handle, out var s))
        {
            _log.LogDebug("XFS event {EventId} for unknown handle {Handle} dropped", e.EventId, e.Handle);
            return;
        }
        // Keep this callback trivial: it runs on the XFS message-pump thread.
        EventReceived?.Invoke(new XfsEvent(e.Kind, s.Name, s.Class, e.EventId, e.HResult, e.Data, e.At));
    }

    public Task<ushort> OpenAsync(string logicalName, DeviceClass deviceClass, CancellationToken ct) =>
        Task.Run(() =>
        {
            EnsureStarted();
            using var act = Activity.StartActivity("xfs.open");
            act?.SetTag("xfs.logical_name", logicalName);
            var info = _api.Open(logicalName, deviceClass, _apiOpt.OpenTimeout);
            _sessions[info.Handle] = new Session(logicalName, deviceClass);
            _log.LogInformation("XFS service {LogicalName} opened: handle={Handle} spiVersion={Spi} desc={Description}",
                logicalName, info.Handle, info.SpiVersion, info.Description);
            return info.Handle;
        }, ct);

    public async Task RegisterAsync(ushort handle, XfsEventClass classes, CancellationToken ct)
    {
        var s = Get(handle);
        await WithGate(s, () => Check("WFSRegister", s, _api.Register(handle, classes)), ct).ConfigureAwait(false);
    }

    public async Task LockAsync(ushort handle, TimeSpan timeout, CancellationToken ct)
    {
        var s = Get(handle);
        await WithGate(s, () => Check("WFSLock", s, _api.Lock(handle, timeout)), ct).ConfigureAwait(false);
    }

    public async Task UnlockAsync(ushort handle, CancellationToken ct)
    {
        var s = Get(handle);
        await WithGate(s, () => Check("WFSUnlock", s, _api.Unlock(handle)), ct).ConfigureAwait(false);
    }

    public async Task<XfsResponse> GetInfoAsync(ushort handle, uint category, object? query, CancellationToken ct)
    {
        var s = Get(handle);
        return await WithRetry(s, "WFSGetInfo", category, _opt.MaxInfoAttempts,
            () => _api.GetInfo(handle, category, query, _apiOpt.DefaultTimeout), ct).ConfigureAwait(false);
    }

    public async Task<XfsResponse> ExecuteAsync(ushort handle, uint command, object? data, TimeSpan timeout,
        bool retryable, CancellationToken ct)
    {
        var s = Get(handle);
        return await WithRetry(s, "WFSExecute", command, retryable ? _opt.MaxInfoAttempts : 1,
            () => _api.Execute(handle, command, data, timeout), ct).ConfigureAwait(false);
    }

    public async Task CloseAsync(ushort handle, CancellationToken ct)
    {
        if (!_sessions.TryRemove(handle, out var s)) return;
        await Task.Run(() =>
        {
            var hr = _api.Close(handle);
            if (hr != 0) _log.LogWarning("WFSClose {LogicalName} returned {HResult}", s.Name, hr);
        }, ct).ConfigureAwait(false);
    }

    // ---- helpers ----
    private Session Get(ushort handle) =>
        _sessions.TryGetValue(handle, out var s) ? s : throw new InvalidOperationException($"XFS handle {handle} is not open.");

    private static async Task WithGate(Session s, Action action, CancellationToken ct)
    {
        await s.Gate.WaitAsync(ct).ConfigureAwait(false);
        try { await Task.Run(action, ct).ConfigureAwait(false); }
        finally { s.Gate.Release(); }
    }

    private void Check(string op, Session s, int hr)
    {
        if (hr != 0) throw new XfsException(op, s.Name, hr);
    }

    private async Task<XfsResponse> WithRetry(Session s, string op, uint code, int attempts, Func<XfsResponse> call,
        CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            await s.Gate.WaitAsync(ct).ConfigureAwait(false);
            XfsResponse resp;
            var sw = Stopwatch.StartNew();
            using var act = Activity.StartActivity(op);
            act?.SetTag("xfs.logical_name", s.Name);
            act?.SetTag("xfs.code", code);
            try { resp = await Task.Run(call, ct).ConfigureAwait(false); }
            finally { s.Gate.Release(); }

            var tags = new TagList { { "op", op }, { "device", s.Class.ToString() } };
            Calls.Add(1, tags);
            Latency.Record(sw.Elapsed.TotalMilliseconds, tags);
            if (resp.Success) return resp;

            Errors.Add(1, tags);
            var cls = _classifier.Classify(resp.HResult);
            _log.LogWarning("{Op} {LogicalName} code={Code} failed hResult={HResult} class={Class} attempt={Attempt}/{Max}",
                op, s.Name, code, resp.HResult, cls, attempt, attempts);

            bool retry = attempt < attempts && cls is XfsErrorClass.Transient or XfsErrorClass.Unknown;
            if (!retry) return resp;
            var delay = TimeSpan.FromMilliseconds(_opt.BaseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _api.EventReceived -= OnApiEvent;
        foreach (var h in _sessions.Keys.ToArray())
        {
            try { _api.Close(h); } catch (Exception ex) { _log.LogDebug(ex, "close on dispose failed"); }
        }
        _api.Dispose();
    }
}

public static class XfsResponseExtensions
{
    public static XfsResponse ThrowIfFailed(this XfsResponse r, string operation, string logicalName)
    {
        if (!r.Success) throw new XfsException(operation, logicalName, r.HResult);
        return r;
    }
}
