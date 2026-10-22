using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace AtmNexus.Devices.Xfs.Xfs4Iot;

public sealed class Xfs4IotException(string command, string code, string? description)
    : Exception($"XFS4IoT {command} completed with '{code}'{(description is null ? "" : ": " + description)}")
{
    public string Command { get; } = command;
    public string CompletionCode { get; } = code;
}

/// <summary>
/// Minimal XFS4IoT client: JSON messages over a (local, TLS) WebSocket. Message framing follows the XFS4IoT header
/// {type,name,requestId}. Command names/payload shapes must be validated against the XFS4IoT release implemented by the vendor
/// (e.g. 2022-2 vs 2024-1); isolate any differences in the backend mapper, not here.
/// </summary>
public sealed class Xfs4IotClient(Uri endpoint, ILogger<Xfs4IotClient> log) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _requestId;

    public event Action<string, JsonElement>? EventReceived;
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        // Service endpoints are localhost-only and use a self-signed cert: accept only loopback + pinned thumbprint (configured by caller).
        await _ws.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoop(_cts.Token));
        log.LogInformation("XFS4IoT connected to {Endpoint}", endpoint);
    }

    public async Task<JsonElement> SendCommandAsync(string name, object? payload, TimeSpan timeout, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException("XFS4IoT socket is not connected.");
        int id = Interlocked.Increment(ref _requestId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            header = new { type = "command", name, requestId = id },
            payload = payload ?? new { }
        }, Json);

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendGate.Release(); }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token));
        try
        {
            var completion = await tcs.Task.ConfigureAwait(false);
            string code = completion.TryGetProperty("completionCode", out var cc) ? cc.GetString() ?? "unknown" : "success";
            if (code != "success")
            {
                string? desc = completion.TryGetProperty("errorDescription", out var ed) ? ed.GetString() : null;
                throw new Xfs4IotException(name, code, desc);
            }
            return completion;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _ws is { State: WebSocketState.Open })
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                Dispatch(ms.ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.LogError(ex, "XFS4IoT receive loop terminated");
            foreach (var p in _pending.Values) p.TrySetException(ex);
        }
    }

    private void Dispatch(byte[] frame)
    {
        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        var header = root.GetProperty("header");
        string type = header.GetProperty("type").GetString() ?? "";
        string name = header.GetProperty("name").GetString() ?? "";
        var payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;

        if (type == "completion" && header.TryGetProperty("requestId", out var rid) && _pending.TryGetValue(rid.GetInt32(), out var tcs))
            tcs.TrySetResult(payload);
        else if (type is "event" or "unsolicited")
            EventReceived?.Invoke(name, payload);   // acknowledge frames are ignored
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); }
            catch (WebSocketException) { }
        }
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _ws?.Dispose();
    }
}
