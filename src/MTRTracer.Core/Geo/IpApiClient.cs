using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MTRTracer.Core.Geo;

public sealed class IpApiRecord
{
    public string? Status { get; set; }
    public string? Message { get; set; }
    public string? Query { get; set; }
    public string? Country { get; set; }
    public string? CountryCode { get; set; }
    public string? RegionName { get; set; }
    public string? City { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string? Isp { get; set; }
    public string? Org { get; set; }
    public string? As { get; set; }
    public string? Asname { get; set; }
    [JsonIgnore] public DateTime Fetched { get; set; }
    public long FetchedTicks { get => Fetched.Ticks; set => Fetched = new DateTime(value, DateTimeKind.Utc); }

    public GeoDto? ToGeo() => Status == "success"
        ? new GeoDto(Lat, Lon, City, RegionName, Country, CountryCode, "ipapi", string.IsNullOrEmpty(City) ? "country" : "city")
        : null;
}

/// <summary>
/// Keyless ip-api.com client. Batches up to 100 addresses per request, honours the X-Rl / X-Ttl
/// rate limit headers and caches results on disk for 30 days.
/// </summary>
public sealed class IpApiClient
{
    private const string Fields = "status,message,query,country,countryCode,regionName,city,lat,lon,isp,org,as,asname";
    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, IpApiRecord> _cache = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpApiRecord?>> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private DateTime _blockedUntil = DateTime.MinValue;
    private int _saveScheduled;

    public bool Enabled { get; set; } = true;
    public string? LastError { get; private set; }

    public IpApiClient(HttpClient http, string cacheDir)
    {
        _http = http;
        _cachePath = Path.Combine(cacheDir, "ipapi-cache.json");
        Load();
        _ = Task.Run(PumpAsync);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var items = JsonSerializer.Deserialize<List<IpApiRecord>>(File.ReadAllText(_cachePath), Logging.Json.Options) ?? [];
            foreach (var r in items.Where(r => r.Query != null && DateTime.UtcNow - r.Fetched < TimeSpan.FromDays(30)))
                _cache[r.Query!] = r;
        }
        catch { }
    }

    private void ScheduleSave()
    {
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1) return;
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _saveScheduled, 0);
            try { File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache.Values.ToList(), Logging.Json.Options)); } catch { }
        });
    }

    public IpApiRecord? Cached(string ip) => _cache.TryGetValue(ip, out var r) ? r : null;

    public int CacheCount => _cache.Count;

    public void ClearCache()
    {
        _cache.Clear();
        try { File.Delete(_cachePath); } catch { }
    }

    public Task<IpApiRecord?> LookupAsync(IPAddress ip)
    {
        var key = ip.ToString();
        if (_cache.TryGetValue(key, out var hit)) return Task.FromResult<IpApiRecord?>(hit);
        if (!Enabled) return Task.FromResult<IpApiRecord?>(null);
        var tcs = _pending.GetOrAdd(key, _ => new TaskCompletionSource<IpApiRecord?>(TaskCreationOptions.RunContinuationsAsynchronously));
        _signal.Release();
        return tcs.Task;
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            await _signal.WaitAsync();
            // Collect for a moment so a whole route goes in one request.
            await Task.Delay(400);
            while (_signal.CurrentCount > 0) _signal.Wait(0);
            var wait = _blockedUntil - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);

            var batch = _pending.Keys.Take(100).ToList();
            if (batch.Count == 0) continue;
            try
            {
                using var resp = await _http.PostAsJsonAsync($"http://ip-api.com/batch?fields={Fields}&lang=ru", batch);
                UpdateLimits(resp);
                if ((int)resp.StatusCode == 429)
                {
                    _signal.Release();
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                var records = await resp.Content.ReadFromJsonAsync<List<IpApiRecord>>(Logging.Json.Options) ?? [];
                foreach (var r in records.Where(r => r.Query != null))
                {
                    r.Fetched = DateTime.UtcNow;
                    _cache[r.Query!] = r;
                    if (_pending.TryRemove(r.Query!, out var tcs)) tcs.TrySetResult(r);
                }
                LastError = null;
                ScheduleSave();
            }
            catch (Exception e)
            {
                LastError = e.Message;
                foreach (var ip in batch)
                    if (_pending.TryRemove(ip, out var tcs)) tcs.TrySetResult(null);
            }
            if (!_pending.IsEmpty) _signal.Release();
        }
    }

    private void UpdateLimits(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("X-Rl", out var rl) && int.TryParse(rl.FirstOrDefault(), out var remaining) && remaining <= 0
            && resp.Headers.TryGetValues("X-Ttl", out var ttl) && int.TryParse(ttl.FirstOrDefault(), out var secs))
            _blockedUntil = DateTime.UtcNow.AddSeconds(secs + 1);
        else if ((int)resp.StatusCode == 429)
            _blockedUntil = DateTime.UtcNow.AddSeconds(60);
    }

    /// <summary>Public IP and location as seen from the given source address (adapter binding).</summary>
    public static async Task<IpApiRecord?> SelfAsync(IPAddress? source, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            ConnectCallback = async (ctx, token) =>
            {
                var addrs = await System.Net.Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, AddressFamily.InterNetwork, token);
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    if (source is { AddressFamily: AddressFamily.InterNetwork }) s.Bind(new IPEndPoint(source, 0));
                    await s.ConnectAsync(new IPEndPoint(addrs[0], ctx.DnsEndPoint.Port), token);
                    return new NetworkStream(s, true);
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        try
        {
            var r = await http.GetFromJsonAsync<IpApiRecord>($"http://ip-api.com/json/?fields={Fields}&lang=ru", Logging.Json.Options, ct);
            if (r != null) r.Fetched = DateTime.UtcNow;
            return r?.Status == "success" ? r : null;
        }
        catch
        {
            return null;
        }
    }
}
