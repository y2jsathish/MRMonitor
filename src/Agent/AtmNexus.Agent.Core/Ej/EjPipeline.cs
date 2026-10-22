using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AtmNexus.Agent.Cache;

namespace AtmNexus.Agent.Ej;

public interface IEjSink
{
    Task SendAsync(IReadOnlyList<EjTransaction> transactions, CancellationToken ct);
}

/// <summary>Builds Elasticsearch _bulk NDJSON: monthly indices (atm-ej-YYYY.MM), deterministic _id for idempotent re-indexing.</summary>
public static class EJIndexer
{
    public static string IndexFor(EjTransaction t) => $"atm-ej-{(t.Start ?? DateTimeOffset.UtcNow):yyyy.MM}";

    public static string BuildBulkBody(IEnumerable<EjTransaction> transactions)
    {
        var sb = new StringBuilder();
        foreach (var t in transactions)
        {
            sb.Append(JsonSerializer.Serialize(new { index = new { _index = IndexFor(t), _id = t.Id } })).Append('\n');
            sb.Append(JsonSerializer.Serialize(new
            {
                atm_code = t.AtmCode, vendor = t.Vendor, type = t.Type, start = t.Start, end = t.End,
                masked_pan = t.MaskedPan, amount = t.Amount, currency = t.Currency,
                outcome = t.Outcome.ToString(), error_codes = t.ErrorCodes, notes_presented = t.NotesPresented,
                file = t.File, lines = t.Lines, indexed_at = DateTimeOffset.UtcNow
            }, ContractJson.Options)).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>Production sink on the ATM: store-and-forward through the SQLite outbox; the uploader POSTs to /api/agent/ej.</summary>
public sealed class GatewayEjSink(IOutbox outbox, IUploadSignal signal, IOptions<AgentOptions> agent) : IEjSink
{
    public async Task SendAsync(IReadOnlyList<EjTransaction> transactions, CancellationToken ct)
    {
        if (transactions.Count == 0) return;
        var json = JsonSerializer.Serialize(new { atmCode = agent.Value.AtmCode, transactions }, ContractJson.Options);
        await outbox.EnqueueAsync("ej", json, ct).ConfigureAwait(false);
        signal.Trigger();
    }
}

/// <summary>Direct Elasticsearch sink: used by the server-side EJ indexer consumer and by lab/on-prem single-box installs.</summary>
public sealed class ElasticEjSink(HttpClient http) : IEjSink
{
    public async Task SendAsync(IReadOnlyList<EjTransaction> transactions, CancellationToken ct)
    {
        if (transactions.Count == 0) return;
        using var content = new StringContent(EJIndexer.BuildBulkBody(transactions), Encoding.UTF8, "application/x-ndjson");
        using var resp = await http.PostAsync("_bulk", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.TryGetProperty("errors", out var err) && err.GetBoolean())
            throw new InvalidOperationException("Elasticsearch bulk response reported item errors; batch will be retried (ids are idempotent).");
    }
}

/// <summary>Sends a batch, THEN advances the checkpoint: at-least-once delivery, deduplicated by deterministic ids.</summary>
public sealed class EJStorageService(IEjSink sink, ICheckpointStore checkpoints)
{
    public static string Key(EjSource s, string file) => $"ej:{s.Name}:{Path.GetFileName(file)}";

    public async Task StoreAsync(EjSource source, string file, IReadOnlyList<EjTransaction> batch, long safeOffset, CancellationToken ct)
    {
        await sink.SendAsync(batch, ct).ConfigureAwait(false);
        await checkpoints.SetAsync(Key(source, file), safeOffset, null, ct).ConfigureAwait(false);
    }
}

/// <summary>EJCollector: provider -> parser -> storage, per configured journal source.</summary>
public sealed class EJCollector(
    IEnumerable<IEJProvider> providers, EJStorageService storage, ICheckpointStore checkpoints, IEjSink sink,
    IOptions<EjOptions> ejOptions, IOptions<AgentOptions> agent, ILogger<EJCollector> log) : BackgroundService, IEjCollectionTrigger
{
    private readonly EjOptions _o = ejOptions.Value;
    private readonly Dictionary<string, IEJParser> _parsers = new();
    // In-memory read position per file. It can be ahead of the durable checkpoint while a transaction is open:
    // the checkpoint only advances to the open transaction's start, so a restart replays it, a live pass does not.
    private readonly Dictionary<string, long> _readPos = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_o.Interval);
            do
            {
                foreach (var s in _o.Sources)
                {
                    try { await CollectSourceAsync(s, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "EJ collection failed for {Source}", s.Name); }
                }
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private IEJParser ParserFor(EjSource s, string file)
    {
        var key = $"{s.Name}:{Path.GetFileName(file)}";
        if (!_parsers.TryGetValue(key, out var p))
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(_o.AtmTimeZoneId);
            p = _parsers[key] = new ProfileEjParser(EjProfile.For(s.Vendor), agent.Value.AtmCode, tz, s.DecimalComma, _o.MaxLinesPerTransaction);
        }
        return p;
    }

    public async Task<int> CollectSourceAsync(EjSource s, CancellationToken ct)
    {
        var provider = providers.FirstOrDefault(p => p.Supports(s)) ?? throw new InvalidOperationException($"No EJ provider for {s.Vendor}");
        int total = 0;
        foreach (var file in provider.ListFiles(s))
        {
            var key = $"{s.Name}:{Path.GetFileName(file)}";
            long offset;
            if (_readPos.TryGetValue(key, out var live)) offset = live;
            else offset = (await checkpoints.GetAsync(EJStorageService.Key(s, file), ct).ConfigureAwait(false))?.Offset ?? 0;

            if (new FileInfo(file).Length < offset)          // truncated / rotated in place: start over with a clean parser
            {
                _parsers.Remove(key);
                _readPos.Remove(key);
                offset = 0;
            }

            var parser = ParserFor(s, file);
            var lines = await Task.Run(() => provider.ReadLines(s, file, offset).ToList(), ct).ConfigureAwait(false);
            if (lines.Count == 0) continue;

            var batch = new List<EjTransaction>();
            foreach (var line in lines)
            {
                batch.AddRange(parser.Feed(line));
                if (batch.Count >= _o.BatchSize)
                {
                    await storage.StoreAsync(s, file, batch, parser.SafeOffset, ct).ConfigureAwait(false);
                    total += batch.Count;
                    batch = new List<EjTransaction>();
                }
            }
            await storage.StoreAsync(s, file, batch, parser.SafeOffset, ct).ConfigureAwait(false);
            total += batch.Count;
            _readPos[key] = lines[^1].EndOffset;
        }
        if (total > 0) log.LogInformation("EJ source {Source}: {Count} transactions shipped", s.Name, total);
        return total;
    }

    public async Task<int> CollectAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        int total = 0;
        var tz = TimeZoneInfo.FindSystemTimeZoneById(_o.AtmTimeZoneId);
        foreach (var s in _o.Sources)
        {
            var provider = providers.FirstOrDefault(p => p.Supports(s));
            if (provider is null) continue;
            foreach (var file in provider.ListFiles(s).Where(f => InRange(f, from, to, tz)))
            {
                // fresh parser, offset 0: independent of the live checkpoint
                var parser = new ProfileEjParser(EjProfile.For(s.Vendor), agent.Value.AtmCode, tz, s.DecimalComma, _o.MaxLinesPerTransaction);
                var txs = new List<EjTransaction>();
                foreach (var line in await Task.Run(() => provider.ReadLines(s, file, 0).ToList(), ct).ConfigureAwait(false))
                    txs.AddRange(parser.Feed(line));
                for (int i = 0; i < txs.Count; i += _o.BatchSize)
                    await sink.SendAsync(txs.Skip(i).Take(_o.BatchSize).ToList(), ct).ConfigureAwait(false);
                total += txs.Count;
            }
        }
        return total;
    }

    private static bool InRange(string file, DateOnly from, DateOnly to, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(File.GetLastWriteTimeUtc(file), tz);
        var d = DateOnly.FromDateTime(local);
        return d >= from && d <= to.AddDays(1);      // a journal closed just after midnight still holds the previous day
    }
}
