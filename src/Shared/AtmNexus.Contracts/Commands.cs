using System.Security.Cryptography;
using System.Text;

namespace AtmNexus.Contracts;

/// <summary>
/// A remote action addressed to exactly one ATM. The platform signs the canonical form with ECDSA P-256;
/// the agent verifies the signature against pinned public keys before doing anything.
/// </summary>
public sealed record RemoteCommand(
    Guid CommandId,
    string AtmCode,
    RemoteCommandType Type,
    IReadOnlyDictionary<string, string> Parameters,
    string IssuedBy,
    string? ApprovedBy,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Nonce)
{
    /// <summary>Deterministic, injection-safe serialisation that is signed (not the JSON wire form).</summary>
    public string Canonical()
    {
        var sb = new StringBuilder(256);
        sb.Append("v1\n")
          .Append(CommandId.ToString("D")).Append('\n')
          .Append(Esc(AtmCode)).Append('\n')
          .Append(Type).Append('\n')
          .Append(Esc(IssuedBy)).Append('\n')
          .Append(Esc(ApprovedBy ?? string.Empty)).Append('\n')
          .Append(IssuedAt.ToUnixTimeSeconds()).Append('\n')
          .Append(ExpiresAt.ToUnixTimeSeconds()).Append('\n')
          .Append(Esc(Nonce));
        foreach (var kv in Parameters.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append('\n').Append(Esc(kv.Key)).Append('=').Append(Esc(kv.Value));
        return sb.ToString();
    }

    private static string Esc(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal)
         .Replace("\n", "\\n", StringComparison.Ordinal)
         .Replace("=", "\\=", StringComparison.Ordinal);
}

public sealed record SignedRemoteCommand(RemoteCommand Command, string KeyId, string Signature);

public sealed record RemoteCommandResult(
    Guid CommandId,
    CommandStatus Status,
    string? Output,
    string? Error,
    DateTimeOffset CompletedAt);

public static class CommandCrypto
{
    public static SignedRemoteCommand Sign(RemoteCommand command, ECDsa privateKey, string keyId)
    {
        var sig = privateKey.SignData(
            Encoding.UTF8.GetBytes(command.Canonical()),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new SignedRemoteCommand(command, keyId, Convert.ToBase64String(sig));
    }

    public static bool Verify(SignedRemoteCommand signed, ECDsa publicKey)
    {
        try
        {
            return publicKey.VerifyData(
                Encoding.UTF8.GetBytes(signed.Command.Canonical()),
                Convert.FromBase64String(signed.Signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
