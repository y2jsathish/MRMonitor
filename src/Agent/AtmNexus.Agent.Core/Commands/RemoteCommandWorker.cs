using System.Net;
using System.Net.Http.Json;

namespace AtmNexus.Agent.Commands;

/// <summary>
/// Pull model: the ATM long-polls the gateway for the next signed command, so no inbound port is ever opened on the ATM.
/// </summary>
public sealed class RemoteCommandWorker(
    IHttpClientFactory http, RemoteCommandDispatcher dispatcher, IOptions<AgentOptions> options,
    ILogger<RemoteCommandWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cmd = await NextAsync(ct).ConfigureAwait(false);
                failures = 0;
                if (cmd is null) continue;

                var result = await dispatcher.ExecuteAsync(cmd, ct).ConfigureAwait(false);
                await ReportAsync(result, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failures = Math.Min(failures + 1, 8);
                log.LogWarning(ex, "Command poll failed ({Failures})", failures);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, failures) * 3)), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<SignedRemoteCommand?> NextAsync(CancellationToken ct)
    {
        using var client = http.CreateClient(Workers.TelemetryUploader.ClientName);
        client.Timeout = options.Value.CommandLongPoll + TimeSpan.FromSeconds(15);
        using var resp = await client.GetAsync($"/api/agent/commands/next?waitSeconds={(int)options.Value.CommandLongPoll.TotalSeconds}", ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NoContent) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SignedRemoteCommand>(ContractJson.Options, ct).ConfigureAwait(false);
    }

    private async Task ReportAsync(RemoteCommandResult result, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                using var client = http.CreateClient(Workers.TelemetryUploader.ClientName);
                using var resp = await client.PostAsJsonAsync($"/api/agent/commands/{result.CommandId}/result", result, ContractJson.Options, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException ex) { log.LogWarning(ex, "Result upload attempt {Attempt} failed", attempt); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct).ConfigureAwait(false);
        }
        log.LogError("Could not report result of command {CommandId}; the server will mark it timed out", result.CommandId);
    }
}
