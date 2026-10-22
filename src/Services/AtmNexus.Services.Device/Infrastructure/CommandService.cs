using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using AtmNexus.Services.Device.Application;

namespace AtmNexus.Services.Device.Infrastructure;

public sealed class SigningOptions
{
    public const string Section = "CommandSigning";
    /// <summary>PEM (PKCS#8 EC P-256) mounted from Key Vault / CSI secret store. In production prefer HSM-backed Key Vault signing.</summary>
    public string? PrivateKeyPemPath { get; set; }
    public string KeyId { get; set; } = "cmd-dev";
    public TimeSpan CommandLifetime { get; set; } = TimeSpan.FromMinutes(15);
}

public interface ICommandSigner
{
    string KeyId { get; }
    SignedRemoteCommand Sign(RemoteCommand command);
    string PublicKeySpkiBase64 { get; }
}

public sealed class PemCommandSigner : ICommandSigner
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string KeyId { get; }
    public string PublicKeySpkiBase64 => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public PemCommandSigner(IOptions<SigningOptions> options, ILogger<PemCommandSigner> log)
    {
        var o = options.Value;
        KeyId = o.KeyId;
        if (!string.IsNullOrEmpty(o.PrivateKeyPemPath) && File.Exists(o.PrivateKeyPemPath))
            _key.ImportFromPem(File.ReadAllText(o.PrivateKeyPemPath));
        else
            log.LogWarning("No command-signing key configured: using an EPHEMERAL key (development only). Agent trust anchor: {PublicKey}", PublicKeySpkiBase64);
    }

    public SignedRemoteCommand Sign(RemoteCommand command) => CommandCrypto.Sign(command, _key, KeyId);
}

/// <summary>
/// Wakes long-polling agents the moment a command is queued (Postgres LISTEN/NOTIFY), so 50,000 idle agents cost
/// one dedicated connection per service instance instead of 50,000 polling queries per interval.
/// </summary>
public sealed class CommandNotifier(NpgsqlDataSource db, ILogger<CommandNotifier> log) : BackgroundService
{
    public const string Channel = "cmd_queued";
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _waiters = new();

    /// <summary>Register BEFORE querying the database so a notification between "query" and "wait" is not lost.</summary>
    public Task Register(Guid atmId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[atmId] = tcs;
        return tcs.Task;
    }

    public void Unregister(Guid atmId) => _waiters.TryRemove(atmId, out _);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                conn.Notification += (_, e) =>
                {
                    if (Guid.TryParse(e.Payload, out var id) && _waiters.TryRemove(id, out var tcs)) tcs.TrySetResult();
                };
                await using (var cmd = new NpgsqlCommand($"LISTEN {Channel}", conn)) await cmd.ExecuteNonQueryAsync(ct);
                log.LogInformation("Listening on Postgres channel {Channel}", Channel);
                while (!ct.IsCancellationRequested) await conn.WaitAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "LISTEN connection lost; reconnecting in 5s (agents fall back to timed re-checks)");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }
}

public sealed record QueuedCommand(Guid CommandId, string State, DateTimeOffset ExpiresAt);

public sealed class CommandService(
    NpgsqlDataSource db, ICommandSigner signer, CommandNotifier notifier, AuditWriter audit,
    IOptions<SigningOptions> options, TimeProvider clock)
{
    private static readonly HashSet<RemoteCommandType> Disruptive = [RemoteCommandType.RebootAtm, RemoteCommandType.ShutdownAtm];

    private sealed class CommandRow
    {
        public Guid CommandId { get; set; }
        public Guid AtmId { get; set; }
        public string AtmCode { get; set; } = "";
        public string CommandType { get; set; } = "";
        public string ParametersJson { get; set; } = "{}";
        public string IssuedBy { get; set; } = "";
        public string? ApprovedBy { get; set; }
        public DateTime IssuedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string Nonce { get; set; } = "";
        public string? KeyId { get; set; }
        public string? Signature { get; set; }
        public string State { get; set; } = "";
    }

    private const string RowSelect = """
        SELECT c.command_id, c.atm_id, a.atm_code, c.command_type, c.parameters::text AS parameters_json, c.issued_by, c.approved_by,
               c.issued_at, c.expires_at, c.nonce, c.key_id, c.signature, c.state
        FROM remote_command c JOIN atm a ON a.atm_id = c.atm_id
        """;

    public async Task<QueuedCommand> QueueAsync(Guid atmId, RemoteCommandType type, IReadOnlyDictionary<string, string> parameters,
        string issuedBy, string? reason, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var atmCode = await conn.ExecuteScalarAsync<string?>(Sql.Cmd(
            "SELECT atm_code FROM atm WHERE atm_id=@atmId AND commission_status IN ('ACTIVE','MAINTENANCE')", new { atmId }, null, ct))
            ?? throw new NotFoundException("ATM not found or not commissioned.");

        // one outstanding command of the same type per ATM: stops operators double-clicking into two reboots
        if (await conn.ExecuteScalarAsync<bool>(Sql.Cmd(
            "SELECT EXISTS (SELECT 1 FROM remote_command WHERE atm_id=@atmId AND command_type=@t AND state IN ('PENDING_APPROVAL','PENDING','DELIVERED','RUNNING') AND expires_at > now())",
            new { atmId, t = type.ToString() }, null, ct)))
            throw new ConflictException($"A {type} command is already in flight for this ATM.");

        var now = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds());     // seconds: the signed canonical form uses unix seconds
        var cmd = new RemoteCommand(Guid.NewGuid(), atmCode, type, parameters, issuedBy, null, now, now + options.Value.CommandLifetime,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));

        bool needsApproval = Disruptive.Contains(type);
        var signed = needsApproval ? null : signer.Sign(cmd);

        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(Sql.Cmd("""
            INSERT INTO remote_command (command_id, atm_id, command_type, parameters, issued_by, issued_at, expires_at, nonce, key_id, signature, state)
            VALUES (@id, @atmId, @type, @parameters::jsonb, @by, @issued, @expires, @nonce, @keyId, @sig, @state)
            """,
            new
            {
                id = cmd.CommandId, atmId, type = type.ToString(), parameters = Sql.Json(parameters), by = issuedBy,
                issued = cmd.IssuedAt.UtcDateTime, expires = cmd.ExpiresAt.UtcDateTime, nonce = cmd.Nonce,
                keyId = signed?.KeyId, sig = signed?.Signature, state = needsApproval ? "PENDING_APPROVAL" : "PENDING"
            }, tx, ct));
        if (!needsApproval) await conn.ExecuteAsync(Sql.Cmd("SELECT pg_notify(@ch, @payload)", new { ch = CommandNotifier.Channel, payload = atmId.ToString() }, tx, ct));
        await tx.CommitAsync(ct);

        await audit.WriteAsync("atm.command.queue", "remote_command", cmd.CommandId.ToString(), atmId, "SUCCESS",
            new { type = type.ToString(), parameters, reason, needsApproval }, ct);
        return new QueuedCommand(cmd.CommandId, needsApproval ? "PENDING_APPROVAL" : "PENDING", cmd.ExpiresAt);
    }

    /// <summary>Dual control: a DIFFERENT person approves; only then is the command signed (ApprovedBy is part of the signed payload).</summary>
    public async Task<QueuedCommand> ApproveAsync(Guid commandId, string approver, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<CommandRow>(Sql.Cmd(RowSelect + " WHERE c.command_id=@commandId FOR UPDATE OF c", new { commandId }, tx, ct))
                  ?? throw new NotFoundException("Command not found.");
        if (row.State != "PENDING_APPROVAL") throw new ConflictException($"Command is {row.State}, not awaiting approval.");
        if (row.IssuedBy.Equals(approver, StringComparison.OrdinalIgnoreCase)) throw new ForbiddenException("The approver must be a different person from the requester.");
        if (row.ExpiresAt <= DateTime.UtcNow) throw new ConflictException("Command expired before approval.");

        var cmd = ToCommand(row) with { ApprovedBy = approver };
        var signed = signer.Sign(cmd);
        await conn.ExecuteAsync(Sql.Cmd(
            "UPDATE remote_command SET approved_by=@approver, key_id=@k, signature=@s, state='PENDING' WHERE command_id=@commandId",
            new { approver, k = signed.KeyId, s = signed.Signature, commandId }, tx, ct));
        await conn.ExecuteAsync(Sql.Cmd("SELECT pg_notify(@ch, @payload)", new { ch = CommandNotifier.Channel, payload = row.AtmId.ToString() }, tx, ct));
        await tx.CommitAsync(ct);

        await audit.WriteAsync("atm.command.approve", "remote_command", commandId.ToString(), row.AtmId, "SUCCESS", new { approver }, ct);
        return new QueuedCommand(commandId, "PENDING", cmd.ExpiresAt);
    }

    /// <summary>Long-poll for the agent: returns the next signed command or null after <paramref name="wait"/>.</summary>
    public async Task<SignedRemoteCommand?> DequeueAsync(string atmCode, TimeSpan wait, CancellationToken ct)
    {
        var atmId = await ResolveAsync(atmCode, ct);
        var deadline = clock.GetUtcNow() + wait;
        var signal = notifier.Register(atmId);
        try
        {
            while (true)
            {
                if (await TryTakeAsync(atmId, ct) is { } cmd) return cmd;
                var remaining = deadline - clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero) return null;
                // wake on NOTIFY, or re-check every 20 s in case the LISTEN connection is reconnecting
                await Task.WhenAny(signal, Task.Delay(remaining < TimeSpan.FromSeconds(20) ? remaining : TimeSpan.FromSeconds(20), ct));
                if (signal.IsCompleted) signal = notifier.Register(atmId);
            }
        }
        catch (OperationCanceledException) { return null; }
        finally { notifier.Unregister(atmId); }
    }

    private async Task<SignedRemoteCommand?> TryTakeAsync(Guid atmId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<CommandRow>(Sql.Cmd(
            RowSelect + " WHERE c.atm_id=@atmId AND c.state='PENDING' AND c.expires_at > now() ORDER BY c.issued_at LIMIT 1 FOR UPDATE OF c SKIP LOCKED",
            new { atmId }, tx, ct));
        if (row is null) return null;
        await conn.ExecuteAsync(Sql.Cmd("UPDATE remote_command SET state='DELIVERED', delivered_at=now() WHERE command_id=@id", new { id = row.CommandId }, tx, ct));
        await tx.CommitAsync(ct);
        return new SignedRemoteCommand(ToCommand(row), row.KeyId!, row.Signature!);
    }

    public async Task CompleteAsync(string atmCode, Guid commandId, RemoteCommandResult result, CancellationToken ct)
    {
        var atmId = await ResolveAsync(atmCode, ct);
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(Sql.Cmd("""
            UPDATE remote_command SET state=@state, completed_at=now(), output=@output, error=@error
            WHERE command_id=@commandId AND atm_id=@atmId AND state IN ('DELIVERED','RUNNING')
            """,
            new
            {
                state = result.Status.ToString().ToUpperInvariant(), output = Truncate(result.Output), error = Truncate(result.Error), commandId, atmId
            }, null, ct));
        if (rows == 0) throw new NotFoundException("Command not found for this ATM or already completed.");
        await audit.WriteAsync("atm.command.result", "remote_command", commandId.ToString(), atmId,
            result.Status == CommandStatus.Succeeded ? "SUCCESS" : "FAILURE", new { status = result.Status.ToString(), error = Truncate(result.Error) }, ct, actor: $"agent:{atmCode}");
    }

    private static string? Truncate(string? s) => s is { Length: > 8000 } ? s[..8000] : s;

    private async Task<Guid> ResolveAsync(string atmCode, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid?>(Sql.Cmd("SELECT atm_id FROM atm WHERE atm_code=@atmCode", new { atmCode }, null, ct))
               ?? throw new NotFoundException($"ATM '{atmCode}' is not registered.");
    }

    private static RemoteCommand ToCommand(CommandRow r) => new(
        r.CommandId, r.AtmCode, Enum.Parse<RemoteCommandType>(r.CommandType),
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.ParametersJson) ?? new(),
        r.IssuedBy, r.ApprovedBy,
        new DateTimeOffset(DateTime.SpecifyKind(r.IssuedAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(r.ExpiresAt, DateTimeKind.Utc)), r.Nonce);
}
