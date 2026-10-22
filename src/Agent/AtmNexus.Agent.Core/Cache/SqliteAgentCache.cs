using Microsoft.Data.Sqlite;

namespace AtmNexus.Agent.Cache;

public sealed record OutboxItem(long Id, string Kind, string Json, DateTimeOffset CreatedAt, int Attempts);

public interface IOutbox
{
    Task EnqueueAsync(string kind, string json, CancellationToken ct);
    Task<IReadOnlyList<OutboxItem>> PeekAsync(string? kind, int max, CancellationToken ct);
    Task AckAsync(IReadOnlyCollection<long> ids, CancellationToken ct);
    Task<long> CountAsync(CancellationToken ct);
    Task TrimAsync(int maxRows, CancellationToken ct);
}

public interface ICheckpointStore
{
    Task<(long Offset, string? Fingerprint)?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, long offset, string? fingerprint, CancellationToken ct);
}

public interface INonceStore
{
    /// <returns>false if the nonce was already seen (replay).</returns>
    Task<bool> TryRegisterAsync(string nonce, DateTimeOffset expiresAt, CancellationToken ct);
}

/// <summary>
/// Local durable cache on the ATM (SQLite, WAL): store-and-forward outbox so telemetry survives network loss and
/// agent restarts, EJ/log read checkpoints, and the replay-protection nonce table for signed commands.
/// Contains no cardholder data.
/// </summary>
public sealed class SqliteAgentCache : IOutbox, ICheckpointStore, INonceStore, IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private readonly TimeProvider _clock;

    public SqliteAgentCache(string path, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        if (path != ":memory:" && Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
        Exec("""
             CREATE TABLE IF NOT EXISTS outbox(
               id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, json TEXT NOT NULL,
               created_at INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0);
             CREATE INDEX IF NOT EXISTS ix_outbox_kind ON outbox(kind, id);
             CREATE TABLE IF NOT EXISTS checkpoint(
               ck TEXT PRIMARY KEY, pos INTEGER NOT NULL, fingerprint TEXT, updated_at INTEGER NOT NULL);
             CREATE TABLE IF NOT EXISTS command_nonce(
               nonce TEXT PRIMARY KEY, expires_at INTEGER NOT NULL);
             """);
    }

    private void Exec(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    public Task EnqueueAsync(string kind, string json, CancellationToken ct)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO outbox(kind,json,created_at) VALUES($k,$j,$t)";
            c.Parameters.AddWithValue("$k", kind);
            c.Parameters.AddWithValue("$j", json);
            c.Parameters.AddWithValue("$t", Now);
            c.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxItem>> PeekAsync(string? kind, int max, CancellationToken ct)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = kind is null
                ? "SELECT id,kind,json,created_at,attempts FROM outbox ORDER BY id LIMIT $n"
                : "SELECT id,kind,json,created_at,attempts FROM outbox WHERE kind=$k ORDER BY id LIMIT $n";
            if (kind is not null) c.Parameters.AddWithValue("$k", kind);
            c.Parameters.AddWithValue("$n", max);
            var list = new List<OutboxItem>();
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new OutboxItem(r.GetInt64(0), r.GetString(1), r.GetString(2),
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)), r.GetInt32(4)));
            return Task.FromResult<IReadOnlyList<OutboxItem>>(list);
        }
    }

    public Task AckAsync(IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Task.CompletedTask;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var id in ids)
            {
                using var c = _db.CreateCommand();
                c.Transaction = tx;
                c.CommandText = "DELETE FROM outbox WHERE id=$id";
                c.Parameters.AddWithValue("$id", id);
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return Task.CompletedTask;
    }

    public Task<long> CountAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM outbox";
            return Task.FromResult((long)(c.ExecuteScalar() ?? 0L));
        }
    }

    public Task TrimAsync(int maxRows, CancellationToken ct)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "DELETE FROM outbox WHERE id <= (SELECT id FROM outbox ORDER BY id DESC LIMIT 1 OFFSET $n)";
            c.Parameters.AddWithValue("$n", maxRows);
            c.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<(long Offset, string? Fingerprint)?> GetAsync(string key, CancellationToken ct)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT pos, fingerprint FROM checkpoint WHERE ck=$k";
            c.Parameters.AddWithValue("$k", key);
            using var r = c.ExecuteReader();
            (long, string?)? result = r.Read() ? (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1)) : null;
            return Task.FromResult(result);
        }
    }

    public Task SetAsync(string key, long offset, string? fingerprint, CancellationToken ct)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = """
                INSERT INTO checkpoint(ck,pos,fingerprint,updated_at) VALUES($k,$o,$f,$t)
                ON CONFLICT(ck) DO UPDATE SET pos=$o, fingerprint=$f, updated_at=$t
                """;
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$o", offset);
            c.Parameters.AddWithValue("$f", (object?)fingerprint ?? DBNull.Value);
            c.Parameters.AddWithValue("$t", Now);
            c.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<bool> TryRegisterAsync(string nonce, DateTimeOffset expiresAt, CancellationToken ct)
    {
        lock (_gate)
        {
            using (var prune = _db.CreateCommand())
            {
                prune.CommandText = "DELETE FROM command_nonce WHERE expires_at < $now";
                prune.Parameters.AddWithValue("$now", Now);
                prune.ExecuteNonQuery();
            }
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT OR IGNORE INTO command_nonce(nonce, expires_at) VALUES($n,$e)";
            c.Parameters.AddWithValue("$n", nonce);
            c.Parameters.AddWithValue("$e", expiresAt.ToUnixTimeSeconds());
            return Task.FromResult(c.ExecuteNonQuery() == 1);
        }
    }

    public void Dispose() => _db.Dispose();
}
