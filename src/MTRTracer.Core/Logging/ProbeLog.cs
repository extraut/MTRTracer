using System.Text.Json;
using System.Threading.Channels;
using MTRTracer.Core.Protocol;

namespace MTRTracer.Core.Logging;

/// <summary>
/// Raw per-probe and event log of one session. Rows are forwarded to the UI and streamed to an NDJSON file,
/// and the full history is retained in memory for export (bounded).
/// </summary>
public sealed class ProbeLog : IAsyncDisposable
{
    public const int MaxRetained = 500_000;

    private readonly Channel<object> _channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private readonly List<ProbeRowDto> _rows = [];
    private readonly List<TraceEventDto> _events = [];
    private readonly object _lock = new();

    public string? FilePath { get; }

    public ProbeLog(string? directory, string sessionId)
    {
        if (directory != null)
        {
            try
            {
                Directory.CreateDirectory(directory);
                FilePath = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{sessionId}.ndjson");
            }
            catch
            {
                FilePath = null;
            }
        }
        _writer = Task.Run(WriteLoopAsync);
    }

    public void Add(ProbeRowDto row)
    {
        lock (_lock)
        {
            if (_rows.Count >= MaxRetained) _rows.RemoveRange(0, MaxRetained / 10);
            _rows.Add(row);
        }
        _channel.Writer.TryWrite(row);
    }

    public void Add(TraceEventDto evt)
    {
        lock (_lock) _events.Add(evt);
        _channel.Writer.TryWrite(evt);
    }

    public void AddMeta(object meta) => _channel.Writer.TryWrite(meta);

    public List<ProbeRowDto> Rows()
    {
        lock (_lock) return [.. _rows];
    }

    public List<TraceEventDto> Events()
    {
        lock (_lock) return [.. _events];
    }

    private async Task WriteLoopAsync()
    {
        if (FilePath == null)
        {
            await foreach (var _ in _channel.Reader.ReadAllAsync()) { }
            return;
        }
        await using var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
        await using var writer = new StreamWriter(stream);
        var opts = Json.Options;
        while (await _channel.Reader.WaitToReadAsync())
        {
            while (_channel.Reader.TryRead(out var item))
            {
                var kind = item switch { ProbeRowDto => "probe", TraceEventDto => "event", _ => "meta" };
                await writer.WriteLineAsync($"{{\"kind\":\"{kind}\",\"data\":{JsonSerializer.Serialize(item, item.GetType(), opts)}}}");
            }
            await writer.FlushAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _writer.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
}
