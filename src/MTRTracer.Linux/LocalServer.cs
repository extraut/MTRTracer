using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MTRTracer.Core;
using MTRTracer.Core.Logging;
using MTRTracer.Core.Protocol;

namespace MTRTracer.Linux;

/// <summary>
/// Loopback-only HTTP server: serves the embedded React UI and a WebSocket bridge (/bridge) that carries the
/// same {kind:"req"|"res"|"evt"} messages WebView2 uses on Windows. The bridge requires a random per-launch
/// token and a same-origin request, so other local programs and web pages cannot drive the app.
/// </summary>
public sealed class LocalServer : IEventSink, IAsyncDisposable
{
    private static readonly HashSet<string> Coalesced = ["trace.hops", "route.geo", "adapters.traffic", "connections.snapshot", "geo.status"];

    private readonly HttpListener _listener = new();
    private readonly Assembly _asm = Assembly.GetExecutingAssembly();
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly ConcurrentQueue<(string Type, object Payload)> _queue = new();
    private readonly ConcurrentDictionary<string, object> _latest = new();
    private readonly ConcurrentQueue<object> _probes = new();
    private readonly Timer _flush;
    private readonly CancellationTokenSource _cts = new();
    private HostCommands? _commands;

    public int Port { get; }
    public string Token { get; } = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
    public string Origin => $"http://127.0.0.1:{Port}";
    public string AppUrl => $"{Origin}/index.html?bridge=ws&token={Token}";
    public int ClientCount => _clients.Count;

    private sealed class Client(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    public LocalServer(int port)
    {
        Port = port > 0 ? port : FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _flush = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public void Start(HostCommands commands)
    {
        _commands = commands;
        _listener.Start();
        _flush.Change(50, 50);
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path == "/bridge")
            {
                var origin = ctx.Request.Headers["Origin"];
                if (!ctx.Request.IsWebSocketRequest || ctx.Request.QueryString["token"] != Token || (origin != null && origin != Origin))
                {
                    ctx.Response.StatusCode = 403;
                    ctx.Response.Close();
                    return;
                }
                var ws = await ctx.AcceptWebSocketAsync(null);
                await ClientLoopAsync(ws.WebSocket);
                return;
            }
            Serve(ctx, path == "/" ? "/index.html" : path);
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private void Serve(HttpListenerContext ctx, string path)
    {
        var name = "ui/" + Uri.UnescapeDataString(path.TrimStart('/'));
        var stream = _asm.GetManifestResourceStream(name) ?? _asm.GetManifestResourceStream(name.Replace('/', '\\'));
        var res = ctx.Response;
        if (stream == null)
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }
        res.ContentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            _ => "application/octet-stream",
        };
        res.Headers["Cache-Control"] = "no-cache";
        res.Headers["X-Content-Type-Options"] = "nosniff";
        using (stream) stream.CopyTo(res.OutputStream);
        res.Close();
    }

    private async Task ClientLoopAsync(WebSocket ws)
    {
        var id = Guid.NewGuid();
        var client = new Client(ws);
        _clients[id] = client;
        var buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                var json = Encoding.UTF8.GetString(ms.ToArray());
                _ = Task.Run(async () =>
                {
                    var reply = await _commands!.HandleAsync(json);
                    if (reply != null) await SendAsync(client, reply);
                });
            }
        }
        catch
        {
            // Client went away.
        }
        finally
        {
            _clients.TryRemove(id, out _);
            try { ws.Dispose(); } catch { }
        }
    }

    private static async Task SendAsync(Client c, string json)
    {
        await c.SendLock.WaitAsync();
        try
        {
            if (c.Socket.State == WebSocketState.Open)
                await c.Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { }
        finally
        {
            c.SendLock.Release();
        }
    }

    // ---------------- IEventSink: same coalescing as the Windows host ----------------

    public void Emit(string type, object payload)
    {
        if (type == "trace.probe") _probes.Enqueue(payload);
        else if (Coalesced.Contains(type)) _latest[type] = payload;
        else _queue.Enqueue((type, payload));
    }

    private int _flushing;

    private void Flush()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try
        {
            var messages = new List<string>();
            while (_queue.TryDequeue(out var e)) messages.Add(Event(e.Type, e.Payload));
            if (!_probes.IsEmpty)
            {
                var rows = new List<object>();
                while (rows.Count < 5000 && _probes.TryDequeue(out var r)) rows.Add(r);
                messages.Add(Event("trace.probes", new { rows }));
            }
            foreach (var key in _latest.Keys)
                if (_latest.TryRemove(key, out var payload)) messages.Add(Event(key, payload));
            if (messages.Count == 0 || _clients.IsEmpty) return;
            foreach (var c in _clients.Values)
                foreach (var m in messages)
                    SendAsync(c, m).GetAwaiter().GetResult();
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    private static string Event(string type, object payload) => JsonSerializer.Serialize(new { kind = "evt", type, payload }, Json.Options);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _flush.DisposeAsync();
        try { _listener.Stop(); } catch { }
        foreach (var c in _clients.Values)
        {
            try { await c.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
        }
    }
}
