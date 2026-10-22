using System.Net;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace AtmNexus.Services.Device.Infrastructure;

public sealed class AuditWriter(NpgsqlDataSource db, IHttpContextAccessor http, ILogger<AuditWriter> log)
{
    /// <summary>Appends to the hash-chained audit_log. Failure to audit a privileged action must fail the action, so exceptions propagate.</summary>
    public async Task WriteAsync(string action, string entityType, string? entityId, Guid? atmId, string outcome, object? details,
        CancellationToken ct, string? actor = null)
    {
        var ctx = http.HttpContext;
        string actorId = actor ?? ctx?.User.FindFirst("sub")?.Value ?? "system";
        string? actorName = ctx?.User.FindFirst("preferred_username")?.Value;
        IPAddress? ip = ctx?.Connection.RemoteIpAddress;

        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Sql.Cmd(
            "SELECT audit_append(@actor, @name, @ip, @action, @et, @eid, @atm, @outcome, @details::jsonb, @corr)",
            new
            {
                actor = actorId, name = actorName, ip, action, et = entityType, eid = entityId, atm = atmId, outcome,
                details = Sql.Json(details ?? new { }), corr = ctx?.TraceIdentifier
            }, null, ct));
        log.LogDebug("Audit {Action} {Outcome} by {Actor}", action, outcome, actorId);
    }
}

public sealed class KafkaOptions
{
    public const string Section = "Kafka";
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string SecurityProtocol { get; set; } = "Plaintext";   // Ssl | SaslSsl in production
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
    public string? SslCaLocation { get; set; }
    public int BatchSize { get; set; } = 200;
}

/// <summary>
/// Transactional-outbox relay: reads unpublished rows with FOR UPDATE SKIP LOCKED (so N replicas share the work without
/// double publishing), produces with idempotence + acks=all, marks published in the same transaction. Delivery is at-least-once;
/// consumers de-duplicate on EventId.
/// </summary>
public sealed class OutboxRelayService(NpgsqlDataSource db, IOptions<KafkaOptions> options, ILogger<OutboxRelayService> log) : BackgroundService
{
    private IProducer<string, string>? _producer;

    private IProducer<string, string> Producer()
    {
        var o = options.Value;
        var cfg = new ProducerConfig
        {
            BootstrapServers = o.BootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,
            LingerMs = 5,
            CompressionType = CompressionType.Zstd,
            MessageTimeoutMs = 30_000,
            SecurityProtocol = Enum.Parse<SecurityProtocol>(o.SecurityProtocol, true),
            SaslUsername = o.SaslUsername, SaslPassword = o.SaslPassword, SslCaLocation = o.SslCaLocation
        };
        if (!string.IsNullOrEmpty(o.SaslUsername)) cfg.SaslMechanism = SaslMechanism.Plain;
        return _producer ??= new ProducerBuilder<string, string>(cfg).Build();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int published = 0;
            try { published = await RelayBatchAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Outbox relay failed; retrying"); }
            if (published == 0) await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }
        _producer?.Flush(TimeSpan.FromSeconds(10));
        _producer?.Dispose();
    }

    private async Task<int> RelayBatchAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rows = (await conn.QueryAsync<(long Id, string Topic, string Key, string Payload, string? Correlation)>(Sql.Cmd(
            "SELECT outbox_id AS id, topic, event_key AS key, payload::text AS payload, payload->>'correlationId' AS correlation FROM outbox_event WHERE published_at IS NULL " +
            "ORDER BY outbox_id LIMIT @n FOR UPDATE SKIP LOCKED", new { n = options.Value.BatchSize }, tx, ct))).ToList();
        if (rows.Count == 0) return 0;

        var producer = Producer();
        var sent = new List<long>();
        foreach (var r in rows)
        {
            try
            {
                var message = new Message<string, string> { Key = r.Key, Value = r.Payload, Headers = new Headers { { "schema", System.Text.Encoding.UTF8.GetBytes(r.Topic + "/1") } } };
                if (r.Correlation is { Length: 32 } trace && trace.All(Uri.IsHexDigit))
                    message.Headers.Add("traceparent", System.Text.Encoding.UTF8.GetBytes($"00-{trace}-{Guid.NewGuid().ToString("N")[..16]}-01"));
                await producer.ProduceAsync(r.Topic, message, ct);
                sent.Add(r.Id);
            }
            catch (ProduceException<string, string> ex)
            {
                log.LogWarning(ex, "Kafka produce failed for outbox {Id} on {Topic}", r.Id, r.Topic);
                await conn.ExecuteAsync(Sql.Cmd("UPDATE outbox_event SET attempts = attempts + 1 WHERE outbox_id = @id", new { id = r.Id }, tx, ct));
                break;                                    // preserve ordering: do not skip ahead of a failed event
            }
        }
        if (sent.Count > 0)
            await conn.ExecuteAsync(Sql.Cmd("UPDATE outbox_event SET published_at = now() WHERE outbox_id = ANY(@ids)", new { ids = sent.ToArray() }, tx, ct));
        await tx.CommitAsync(ct);
        return sent.Count;
    }
}

public sealed class OfflineDetectorOptions
{
    public const string Section = "OfflineDetector";
    public TimeSpan OfflineAfter { get; set; } = TimeSpan.FromMinutes(3);   // 3 missed 60-second heartbeats
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Heartbeat watchdog. ATMs that stop reporting flip to OFFLINE; atm.offline is published exactly once per outage because the
/// UPDATE only matches rows that are still ONLINE (safe with multiple replicas).
/// </summary>
public sealed class OfflineDetectorService(NpgsqlDataSource db, IOptions<OfflineDetectorOptions> options, TimeProvider clock, ILogger<OfflineDetectorService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try { await DetectAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Offline detection failed"); }
        }
    }

    public async Task<int> DetectAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - options.Value.OfflineAfter;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var gone = (await conn.QueryAsync<(Guid AtmId, string AtmCode, DateTime LastSeen)>(Sql.Cmd("""
            UPDATE atm_status s SET connectivity = 'OFFLINE', last_state_change_at = now(), updated_at = now()
            FROM atm a
            WHERE a.atm_id = s.atm_id AND s.connectivity = 'ONLINE' AND s.last_seen_at < @cutoff AND a.commission_status = 'ACTIVE'
            RETURNING s.atm_id, a.atm_code, s.last_seen_at
            """, new { cutoff = cutoff.UtcDateTime }, tx, ct))).ToList();

        foreach (var g in gone)
            await Sql.EnqueueEventAsync(conn, tx, Topics.AtmOffline, g.AtmId, "atm.offline",
                new AtmOffline(g.AtmCode, new DateTimeOffset(DateTime.SpecifyKind(g.LastSeen, DateTimeKind.Utc)),
                    (int)Math.Round(options.Value.OfflineAfter.TotalSeconds / 60)), clock, ct);
        await tx.CommitAsync(ct);
        if (gone.Count > 0) log.LogWarning("{Count} ATMs marked OFFLINE", gone.Count);
        return gone.Count;
    }
}
