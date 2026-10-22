using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AtmNexus.Agent.Cache;
using AtmNexus.Agent.Ej;
using AtmNexus.Agent.Health;
using AtmNexus.Agent.Workers;

namespace AtmNexus.Agent.ProBase;

public sealed class ProBaseOptions
{
    public const string Section = "ProBase";

    public bool Enabled { get; set; }
    public string LogDirectory { get; set; } = @"C:\ProBase\Log";
    public string JournalDirectory { get; set; } = @"C:\ProBase\Journal";
    public string LogPattern { get; set; } = "*.log";
    public string JournalPattern { get; set; } = "*.jrn";
    public string Encoding { get; set; } = "utf-8";

    /// <summary>When a file is seen for the first time, start at its end (history is not replayed as alarms).</summary>
    public bool StartAtEnd { get; set; } = true;

    // Dialect (ProBase installations differ by release/customisation - tune after capturing real logs).
    public string LogLineRegex { get; set; } =
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}[.,]\d{3})\s*\|\s*(?<lvl>[A-Za-z]+)\s*\|\s*(?<mod>[\w.\-]+)\s*\|\s*(?<code>[\w-]*)\s*\|\s*(?<msg>.*)$";
    public string TransactionRegex { get; set; } =
        @"\bTX\s+(?<id>\d+)\s+(?<state>STARTED|COMPLETED|CANCELLED|FAILED|TIMEOUT)\b";
    public string OutOfServiceRegex { get; set; } = @"\bOUT OF SERVICE\b";
    public string InServiceRegex { get; set; } = @"\bIN SERVICE\b";

    public TimeSpan ErrorWindow { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan EvaluateEvery { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan FallbackScanEvery { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Per error-code overrides: code -> severity (e.g. "E5001": "Critical").</summary>
    public Dictionary<string, AlertSeverity> CodeSeverity { get; set; } = new();
}

public enum TxState { Started, Completed, Cancelled, Failed, Timeout }

public sealed record ProBaseLogEntry(
    DateTimeOffset At, string Level, string Module, string Code, string Message, string? TxId, TxState? Tx, bool? InService)
{
    public bool IsError => Level.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
                           || Level.Equals("FATAL", StringComparison.OrdinalIgnoreCase)
                           || Level.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase);
    public bool IsFatal => Level.Equals("FATAL", StringComparison.OrdinalIgnoreCase) || Level.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Log Parser Service: one line -> structured entry. All dialect knowledge is configuration.</summary>
public sealed class ProBaseLogParser
{
    private readonly Regex _line, _tx, _oos, _ins;
    private readonly TimeProvider _clock;

    public ProBaseLogParser(IOptions<ProBaseOptions> options, TimeProvider? clock = null)
    {
        var o = options.Value;
        var ro = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
        var to = TimeSpan.FromMilliseconds(200);
        _line = new Regex(o.LogLineRegex, ro, to);
        _tx = new Regex(o.TransactionRegex, ro, to);
        _oos = new Regex(o.OutOfServiceRegex, ro, to);
        _ins = new Regex(o.InServiceRegex, ro, to);
        _clock = clock ?? TimeProvider.System;
    }

    public ProBaseLogEntry? Parse(string rawLine)
    {
        var text = PanMasker.Sanitize(rawLine);      // application logs sometimes echo card data on errors
        var m = _line.Match(text);
        if (!m.Success) return null;

        DateTimeOffset at = DateTime.TryParse(m.Groups["ts"].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
            ? new DateTimeOffset(dt, TimeSpan.Zero) : _clock.GetUtcNow();

        string msg = m.Groups["msg"].Value;
        var tx = _tx.Match(msg);
        TxState? state = tx.Success ? Enum.Parse<TxState>(tx.Groups["state"].Value, ignoreCase: true) : null;
        bool? inService = _oos.IsMatch(msg) ? false : _ins.IsMatch(msg) ? true : null;

        return new ProBaseLogEntry(at, m.Groups["lvl"].Value, m.Groups["mod"].Value, m.Groups["code"].Value, msg,
            tx.Success ? tx.Groups["id"].Value : null, state, inService);
    }
}

/// <summary>
/// Health Monitor Service: derives ApplicationStatus (last-transaction age, errors/hour, in/out of service) from the parsed log stream.
/// Process liveness of the ProBase executables is handled by ServiceMonitorWorker (Agent:Processes).
/// </summary>
public sealed class ProBaseHealthMonitor(AgentStateStore state, IOptions<ProBaseOptions> options, TimeProvider clock) : BackgroundService
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _errors = new();
    private readonly HashSet<string> _openTx = new();
    private DateTimeOffset? _lastTx;
    private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;
    private bool _inService = true;

    /// <summary>True when no customer transaction is open and nothing happened for <paramref name="quiet"/>. Gates disruptive remote commands.</summary>
    public bool IsIdle(TimeSpan quiet)
    {
        lock (_gate) return _openTx.Count == 0 && clock.GetUtcNow() - _lastActivity > quiet;
    }

    public void Observe(ProBaseLogEntry e)
    {
        lock (_gate)
        {
            if (e.Tx is not null)
            {
                _lastActivity = clock.GetUtcNow();
                if (e.TxId is not null)
                {
                    if (e.Tx == TxState.Started) _openTx.Add(e.TxId); else _openTx.Remove(e.TxId);
                }
            }
            if (e.IsError) _errors.Enqueue(e.At);
            if (e.Tx is TxState.Completed or TxState.Failed or TxState.Cancelled or TxState.Timeout) _lastTx = e.At;
            if (e.InService is { } s) _inService = s;
        }
    }

    public ApplicationStatus Evaluate()
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            var window = options.Value.ErrorWindow;
            while (_errors.Count > 0 && now - _errors.Peek() > window) _errors.Dequeue();
            double perHour = _errors.Count / Math.Max(window.TotalHours, 0.01);
            return new ApplicationStatus(true, true, _lastTx is null ? null : now - _lastTx, perHour, _inService);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(options.Value.EvaluateEvery);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var e = Evaluate();
                state.UpdateApplication(a => a with { SinceLastTransaction = e.SinceLastTransaction, ErrorsPerHour = e.ErrorsPerHour, InService = e.InService });
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Event Publisher: turns log entries into alarms (de-duplicated by AlarmPipeline).</summary>
public sealed class ProBaseEventPublisher(AlarmPipeline pipeline, IOptions<ProBaseOptions> options)
{
    public async Task PublishAsync(ProBaseLogEntry e, CancellationToken ct)
    {
        var o = options.Value;
        if (e.Tx == TxState.Failed || e.Tx == TxState.Timeout)
        {
            await pipeline.SubmitAsync(new AgentEvent(Guid.NewGuid(), $"PROBASE_TX_{e.Tx.ToString()!.ToUpperInvariant()}", AlertSeverity.Warning, "probase",
                $"Transaction {e.TxId} {e.Tx}: {e.Message}", e.At, new Dictionary<string, string> { ["txId"] = e.TxId ?? "", ["module"] = e.Module }), ct).ConfigureAwait(false);
            return;
        }
        if (e.InService == false)
        {
            await pipeline.SubmitAsync(new AgentEvent(Guid.NewGuid(), "PROBASE_OUT_OF_SERVICE", AlertSeverity.Major, "probase", e.Message, e.At, null), ct).ConfigureAwait(false);
            return;
        }
        if (!e.IsError) return;

        var sev = o.CodeSeverity.TryGetValue(e.Code, out var s) ? s : e.IsFatal ? AlertSeverity.Critical : AlertSeverity.Major;
        string code = string.IsNullOrEmpty(e.Code) ? "PROBASE_ERROR" : $"PROBASE_ERR_{e.Code.ToUpperInvariant()}";
        await pipeline.SubmitAsync(new AgentEvent(Guid.NewGuid(), code, sev, "probase", e.Message, e.At,
            new Dictionary<string, string> { ["module"] = e.Module, ["code"] = e.Code, ["level"] = e.Level }), ct).ConfigureAwait(false);
    }
}

/// <summary>
/// File Watcher Service: FileSystemWatcher (low latency) + periodic directory scan (safety net for dropped notifications),
/// incremental reads with checkpoints, rotation/truncation handling. Opens files FileShare.ReadWrite - never blocks ProBase.
/// </summary>
public sealed class ProBaseFileWatcher(
    IOptions<ProBaseOptions> options, ProBaseLogParser parser, ProBaseHealthMonitor monitor, ProBaseEventPublisher publisher,
    ICheckpointStore checkpoints, ILogger<ProBaseFileWatcher> log) : BackgroundService
{
    private readonly Channel<string> _changed = Channel.CreateBounded<string>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Dictionary<string, long> _pos = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled) { log.LogInformation("ProBase integration disabled"); return; }

        var watchers = new List<FileSystemWatcher>();
        foreach (var (dir, pattern) in new[] { (o.LogDirectory, o.LogPattern), (o.JournalDirectory, o.JournalPattern) })
        {
            if (!Directory.Exists(dir)) { log.LogWarning("ProBase directory {Dir} does not exist", dir); continue; }
            var w = new FileSystemWatcher(dir, pattern) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = true };
            w.Changed += (_, e) => _changed.Writer.TryWrite(e.FullPath);
            w.Created += (_, e) => _changed.Writer.TryWrite(e.FullPath);
            watchers.Add(w);
        }

        try
        {
            var lastScan = DateTimeOffset.MinValue;
            while (!ct.IsCancellationRequested)
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    wait.CancelAfter(TimeSpan.FromSeconds(5));
                    try { if (await _changed.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false)) while (_changed.Reader.TryRead(out var p)) paths.Add(p); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
                if (DateTimeOffset.UtcNow - lastScan > o.FallbackScanEvery)
                {
                    lastScan = DateTimeOffset.UtcNow;
                    foreach (var (dir, pattern) in new[] { (o.LogDirectory, o.LogPattern), (o.JournalDirectory, o.JournalPattern) })
                        if (Directory.Exists(dir)) foreach (var f in Directory.EnumerateFiles(dir, pattern)) paths.Add(f);
                }
                foreach (var path in paths)
                {
                    try { await ReadNewAsync(path, o, ct).ConfigureAwait(false); }
                    catch (IOException ex) { log.LogDebug(ex, "ProBase file {Path} busy; retry next pass", path); }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { foreach (var w in watchers) w.Dispose(); }
    }

    internal async Task ReadNewAsync(string path, ProBaseOptions o, CancellationToken ct)
    {
        string key = "probase:" + Path.GetFileName(path);
        long length = new FileInfo(path).Length;
        long offset;
        if (_pos.TryGetValue(key, out var live)) offset = live;
        else if (await checkpoints.GetAsync(key, ct).ConfigureAwait(false) is { } cp) offset = cp.Offset;
        else offset = o.StartAtEnd ? length : 0;
        if (length < offset) offset = 0;                      // rotated / truncated

        long last = offset;
        foreach (var line in LineFileReader.ReadLines(path, offset, Encoding.GetEncoding(o.Encoding)))
        {
            last = line.EndOffset;
            var entry = parser.Parse(line.Text);
            if (entry is null) continue;
            monitor.Observe(entry);
            await publisher.PublishAsync(entry, ct).ConfigureAwait(false);
        }
        _pos[key] = last;
        await checkpoints.SetAsync(key, last, null, ct).ConfigureAwait(false);
    }
}
