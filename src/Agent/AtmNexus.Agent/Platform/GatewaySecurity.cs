using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using AtmNexus.Agent.Ej;
using AtmNexus.Agent.Platform;

namespace AtmNexus.Agent.Platform.Windows;

public sealed class AgentSecurityOptions
{
    public const string Section = "Security";
    /// <summary>Thumbprint of the ATM's device certificate in LocalMachine\My (private key in TPM-backed KSP where available).</summary>
    public string ClientCertThumbprint { get; set; } = "";
    public string TokenEndpoint { get; set; } = "https://nexus-idp.example.bank/connect/token";
    public string Scope { get; set; } = "atm.agent";
}

public static class AgentHttp
{
    public static SocketsHttpHandler CreateMtlsHandler(AgentSecurityOptions o)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }
        };
        if (!string.IsNullOrWhiteSpace(o.ClientCertThumbprint))
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var cert = store.Certificates.Find(X509FindType.FindByThumbprint, o.ClientCertThumbprint, validOnly: true).FirstOrDefault()
                       ?? throw new InvalidOperationException($"Device certificate {o.ClientCertThumbprint} not found or not valid in LocalMachine\\My");
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { cert };
        }
        return handler;
    }
}

/// <summary>
/// OAuth2 client-credentials with mutual-TLS client authentication (RFC 8705 tls_client_auth): the ATM's device certificate
/// is the credential, so there is no shared secret on disk. client_id = ATM code. Tokens are cached until 60 s before expiry.
/// </summary>
public sealed class MtlsTokenProvider(
    IHttpClientFactory http, IOptions<AgentSecurityOptions> sec, IOptions<AgentOptions> agent, TimeProvider clock) : IAgentTokenProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken,
                                        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    public async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && clock.GetUtcNow() < _expires - TimeSpan.FromSeconds(60)) return _token;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && clock.GetUtcNow() < _expires - TimeSpan.FromSeconds(60)) return _token;
            using var client = http.CreateClient("token");
            using var resp = await client.PostAsync(sec.Value.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = agent.Value.AtmCode,
                ["scope"] = sec.Value.Scope
            }), ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var tr = await resp.Content.ReadFromJsonAsync<TokenResponse>(ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("empty token response");
            _token = tr.AccessToken;
            _expires = clock.GetUtcNow().AddSeconds(tr.ExpiresIn);
            return _token;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>
/// Zips recent log files from allow-listed directories and uploads them. Text logs are streamed through the PAN/track masker,
/// binary and oversized files are skipped, so a diagnostic bundle can never exfiltrate cardholder data.
/// </summary>
public sealed class SanitizingLogBundleUploader(IHttpClientFactory http, ILogger<SanitizingLogBundleUploader> log) : ILogBundleUploader
{
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase) { ".log", ".txt", ".jrn", ".xml", ".json", ".csv", ".ini" };
    private const long MaxFileBytes = 50L * 1024 * 1024;
    private const long MaxBundleBytes = 200L * 1024 * 1024;

    public async Task<string> BundleAndUploadAsync(IEnumerable<string> directories, TimeSpan lookback, string label, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"nexus-logs-{label}.zip");
        try
        {
            long total = 0;
            int files = 0, dirIndex = 0;
            await using (var zipFs = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(zipFs, ZipArchiveMode.Create))
            {
                var cutoff = DateTime.UtcNow - lookback;
                foreach (var dir in directories.Where(Directory.Exists))
                {
                    dirIndex++;
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
                                 .Where(f => f.LastWriteTimeUtc >= cutoff && TextExt.Contains(f.Extension) && f.Length <= MaxFileBytes))
                    {
                        if (total + f.Length > MaxBundleBytes) break;
                        ct.ThrowIfCancellationRequested();
                        var entry = zip.CreateEntry($"{dirIndex}/{f.Name}", CompressionLevel.Optimal);
                        await using var es = entry.Open();
                        await using var w = new StreamWriter(es);
                        await using var src = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var r = new StreamReader(src);
                        while (await r.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                            await w.WriteLineAsync(PanMasker.Sanitize(line)).ConfigureAwait(false);
                        total += f.Length; files++;
                    }
                }
            }
            log.LogInformation("Log bundle {Label}: {Files} files, {Bytes} bytes uncompressed", label, files, total);

            await using var upload = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var content = new StreamContent(upload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var client = http.CreateClient(Workers.TelemetryUploader.ClientName);
            client.Timeout = TimeSpan.FromMinutes(10);
            using var resp = await client.PutAsync($"/api/agent/logs/{label}", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return resp.Headers.Location?.ToString() ?? $"/api/agent/logs/{label}";
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }
}
