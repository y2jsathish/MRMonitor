using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AtmNexus.Simulator;

/// <summary>
/// Minimal XFS4IoT service endpoint (CashDispenser/CashManagement/Common) over a local WebSocket, backed by <see cref="SimulatedAtm"/>.
/// Used to exercise <c>Xfs4IotCdmBackend</c> without hardware.
/// </summary>
public sealed class MockXfs4IotServer(SimulatedAtm atm, int port) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<WebSocket> _clients = new();
    private Task? _accept;

    public Uri Endpoint => new($"ws://localhost:{port}/xfs4iot/v1.0/");

    public void Start()
    {
        _listener.Prefixes.Add($"http://localhost:{port}/xfs4iot/v1.0/");
        _listener.Start();
        _accept = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
                var ws = (await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;
                lock (_clients) _clients.Add(ws);
                _ = Task.Run(() => Serve(ws));
            }
            catch (Exception) when (_cts.IsCancellationRequested) { break; }
            catch (HttpListenerException) { break; }
        }
    }

    private async Task Serve(WebSocket ws)
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                using var doc = JsonDocument.Parse(ms.ToArray());
                var header = doc.RootElement.GetProperty("header");
                string name = header.GetProperty("name").GetString() ?? "";
                int id = header.GetProperty("requestId").GetInt32();
                var payload = doc.RootElement.TryGetProperty("payload", out var p) ? p : default;
                var completion = Handle(name, payload, out var events);

                await SendAsync(ws, new { header = new { type = "completion", name, requestId = id }, payload = completion }).ConfigureAwait(false);
                foreach (var (evName, evPayload) in events) await Push(evName, evPayload).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private Dictionary<string, object?> Handle(string name, JsonElement payload, out List<(string, object)> events)
    {
        events = new();
        lock (atm.Gate)
        {
            switch (name)
            {
                case "Common.Status":
                    return Ok(("common", new { device = atm.DeviceState == 0 ? "online" : "hardwareError" }),
                              ("cashManagement", new { safeDoor = atm.SafeDoorOpen ? "open" : "closed" }),
                              ("cashDispenser", new { intermediateStacker = "empty" }));

                case "CashManagement.GetCashUnitInfo":
                    var units = atm.Units.ToDictionary(u => $"unit{u.Number}", u => (object)new
                    {
                        type = u.XfsType == 3 ? "billCassette" : "rejectCassette",
                        currency = u.Currency, value = u.Value, initialCount = u.Initial, count = u.Count, rejectCount = u.Reject,
                        minimum = u.Minimum, maximum = u.Maximum, appLock = false,
                        status = u.Status switch { 1 => "full", 2 => "high", 3 => "low", 4 => "empty", _ => "ok" }
                    });
                    return Ok(("units", units));

                case "CashDispenser.Dispense":
                    string? currency = null; long amount = 0;
                    if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("denomination", out var den)
                        && den.TryGetProperty("currencies", out var cur))
                        foreach (var c in cur.EnumerateObject()) { currency = c.Name; amount = c.Value.GetInt64(); }

                    var crossed = new List<SimulatedCashUnit>();
                    if (currency is null || atm.DeviceState != 0 || !atm.TryDispense(currency, (uint)amount, crossed))
                        return new() { ["completionCode"] = "cannotDispense", ["errorDescription"] = "amount cannot be composed" };
                    foreach (var u in crossed)
                        events.Add(("CashManagement.CashUnitThresholdEvent", new { unit = $"unit{u.Number}", status = u.Status == 4 ? "empty" : "low" }));
                    return Ok();

                default:
                    return Ok();
            }
        }
    }

    private static Dictionary<string, object?> Ok(params (string, object)[] fields)
    {
        var d = new Dictionary<string, object?> { ["completionCode"] = "success" };
        foreach (var (k, v) in fields) d[k] = v;
        return d;
    }

    /// <summary>Broadcast an unsolicited event to every connected client (e.g. safe door opened).</summary>
    public async Task Push(string name, object payload)
    {
        WebSocket[] clients;
        lock (_clients) clients = _clients.Where(c => c.State == WebSocketState.Open).ToArray();
        foreach (var c in clients)
            await SendAsync(c, new { header = new { type = "unsolicited", name }, payload }).ConfigureAwait(false);
    }

    private static Task SendAsync(WebSocket ws, object message) =>
        ws.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(message, Json)), WebSocketMessageType.Text, true, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Close();
        if (_accept is not null) { try { await _accept.ConfigureAwait(false); } catch (Exception) { } }
    }
}
