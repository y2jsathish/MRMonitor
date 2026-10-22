using System.Security.Cryptography;
using AtmNexus.Agent.Cache;

namespace AtmNexus.Agent.Commands;

public sealed class CommandPolicy
{
    public const string Section = "Commands";

    public string AtmCode { get; set; } = "";
    /// <summary>keyId -> Base64 SubjectPublicKeyInfo (ECDSA P-256). Two keys are pinned during rotation (current + next).</summary>
    public Dictionary<string, string> TrustedKeys { get; set; } = new();
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public HashSet<RemoteCommandType> Enabled { get; set; } = new(Enum.GetValues<RemoteCommandType>());
    /// <summary>Dual control: these types need ApprovedBy (a second person) inside the signed payload.</summary>
    public HashSet<RemoteCommandType> RequireApproval { get; set; } = [RemoteCommandType.RebootAtm, RemoteCommandType.ShutdownAtm];

    public HashSet<string> AllowedServices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string AtmApplicationService { get; set; } = "";
    public string XfsServiceName { get; set; } = "";
    public List<string> LogDirectories { get; set; } = new();
}

public sealed class CommandRejectedException(string reason) : Exception(reason);

public interface IRemoteCommandHandler
{
    RemoteCommandType Type { get; }
    Task<string> HandleAsync(RemoteCommand command, CancellationToken ct);
}

/// <summary>
/// Zero-trust command executor. Order matters: authenticity -> addressing -> freshness -> authorisation -> replay -> execution.
/// Nothing about a command is acted on before its signature verifies, and only allow-listed command types with validated
/// parameters ever reach a handler (no shell, no free-form scripts).
/// </summary>
public sealed class RemoteCommandDispatcher(
    IOptions<CommandPolicy> policy, INonceStore nonces, IEnumerable<IRemoteCommandHandler> handlers,
    TimeProvider clock, ILogger<RemoteCommandDispatcher> log)
{
    private readonly CommandPolicy _p = policy.Value;
    private readonly Dictionary<RemoteCommandType, IRemoteCommandHandler> _handlers = handlers.ToDictionary(h => h.Type);

    public async Task<RemoteCommandResult> ExecuteAsync(SignedRemoteCommand signed, CancellationToken ct)
    {
        var cmd = signed.Command;
        var now = clock.GetUtcNow();
        RemoteCommandResult Reject(string why, CommandStatus status = CommandStatus.Rejected)
        {
            log.LogWarning("Command {CommandId} {Type} rejected: {Reason}", cmd.CommandId, cmd.Type, why);
            return new RemoteCommandResult(cmd.CommandId, status, null, why, now);
        }

        // 1. authenticity
        if (!_p.TrustedKeys.TryGetValue(signed.KeyId, out var spki)) return Reject($"unknown signing key '{signed.KeyId}'");
        using (var key = ECDsa.Create())
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
            if (!CommandCrypto.Verify(signed, key)) return Reject("invalid signature");
        }

        // 2. addressing (a command signed for ATM A must never run on ATM B)
        if (!string.Equals(cmd.AtmCode, _p.AtmCode, StringComparison.OrdinalIgnoreCase)) return Reject("addressed to a different ATM");

        // 3. freshness
        if (now > cmd.ExpiresAt + _p.MaxClockSkew) return Reject("expired", CommandStatus.Expired);
        if (cmd.IssuedAt > now + _p.MaxClockSkew) return Reject("issued in the future (clock skew)");

        // 4. authorisation (local policy can disable any command type regardless of what the server signs)
        if (!_p.Enabled.Contains(cmd.Type)) return Reject($"{cmd.Type} disabled by local policy");
        if (_p.RequireApproval.Contains(cmd.Type) &&
            (string.IsNullOrWhiteSpace(cmd.ApprovedBy) || string.Equals(cmd.ApprovedBy, cmd.IssuedBy, StringComparison.OrdinalIgnoreCase)))
            return Reject("dual control: a second, different approver is required");

        // 5. replay
        if (!await nonces.TryRegisterAsync(cmd.Nonce, cmd.ExpiresAt + TimeSpan.FromHours(1), ct).ConfigureAwait(false)) return Reject("replay detected");

        // 6. execution
        if (!_handlers.TryGetValue(cmd.Type, out var handler)) return Reject($"no handler for {cmd.Type}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_p.CommandTimeout);
        try
        {
            log.LogInformation("Executing {Type} command {CommandId} issued by {IssuedBy} approved by {ApprovedBy}",
                cmd.Type, cmd.CommandId, cmd.IssuedBy, cmd.ApprovedBy);
            var output = await handler.HandleAsync(cmd, cts.Token).ConfigureAwait(false);
            return new RemoteCommandResult(cmd.CommandId, CommandStatus.Succeeded, output, null, clock.GetUtcNow());
        }
        catch (CommandRejectedException ex) { return Reject(ex.Message); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RemoteCommandResult(cmd.CommandId, CommandStatus.Failed, null, "command timed out", clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Command {CommandId} {Type} failed", cmd.CommandId, cmd.Type);
            return new RemoteCommandResult(cmd.CommandId, CommandStatus.Failed, null, ex.Message, clock.GetUtcNow());
        }
    }
}
