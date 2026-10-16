using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows.Threading;
using MTRTracer.Core.Logging;
using MTRTracer.Core.Protocol;

namespace MTRTracer.App.Bridge;

/// <summary>
/// Collects events from worker threads and forwards them to the page on the UI thread at ~20 Hz.
/// High-rate state (hop snapshots, traffic, connection tables) is coalesced to the latest value;
/// probe rows are batched; everything else is delivered in order.
/// </summary>
public sealed class WebEventSink : IEventSink
{
    private static readonly HashSet<string> Coalesced = ["trace.hops", "route.geo", "adapters.traffic", "connections.snapshot", "geo.status"];

    private readonly ConcurrentQueue<(string Type, object Payload)> _queue = new();
    private readonly ConcurrentDictionary<string, object> _latest = new();
    private readonly ConcurrentQueue<object> _probes = new();
    private readonly DispatcherTimer _timer;
    private Action<string>? _post;

    public WebEventSink(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => Flush(), dispatcher);
    }

    public void Attach(Action<string> post)
    {
        _post = post;
        _timer.Start();
    }

    public void Emit(string type, object payload)
    {
        if (type == "trace.probe") _probes.Enqueue(payload);
        else if (Coalesced.Contains(type)) _latest[type] = payload;
        else _queue.Enqueue((type, payload));
    }

    private void Flush()
    {
        if (_post == null) return;
        // Ordered events first so state transitions precede the snapshots that depend on them.
        while (_queue.TryDequeue(out var e)) Send(e.Type, e.Payload);
        if (!_probes.IsEmpty)
        {
            var rows = new List<object>();
            while (rows.Count < 5000 && _probes.TryDequeue(out var r)) rows.Add(r);
            Send("trace.probes", new { rows });
        }
        foreach (var key in _latest.Keys)
            if (_latest.TryRemove(key, out var payload)) Send(key, payload);
    }

    private void Send(string type, object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { kind = "evt", type, payload }, Json.Options);
            _post!(json);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"event {type} failed: {e.Message}");
        }
    }
}
