using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MTRTracer.Core.Geo;

/// <summary>HttpClient whose connections leave from a specific local address (adapter binding).</summary>
public static class BoundHttp
{
    public static HttpClient Create(IPAddress? source, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (ctx, token) =>
            {
                var addrs = source == null
                    ? await System.Net.Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, token)
                    : await ResolveViaAdapterAsync(ctx.DnsEndPoint.Host, source, token);
                if (addrs.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
                var s = new Socket(addrs[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    if (source != null && source.AddressFamily == addrs[0].AddressFamily) s.Bind(new IPEndPoint(source, 0));
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
        var http = new HttpClient(handler) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MTRTracer/1.1 (+network diagnostics)");
        return http;
    }

    /// <summary>
    /// Resolves a name through the DNS servers of the adapter that owns <paramref name="source"/>, with the
    /// query itself bound to that address. The system resolver may be captured by a VPN client (Xray, sing-box)
    /// and return fake-IP addresses (198.18.0.0/15) that only work inside the tunnel.
    /// </summary>
    public static async Task<IPAddress[]> ResolveViaAdapterAsync(string host, IPAddress source, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];
        var servers = new List<IPAddress>();
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var props = ni.GetIPProperties();
                if (props.UnicastAddresses.Any(u => u.Address.Equals(source)))
                    servers.AddRange(props.DnsAddresses.Where(d => d.AddressFamily == source.AddressFamily));
            }
            catch { }
        }
        servers.AddRange(Dns.ResolverService.PublicResolvers.Where(p => p.AddressFamily == source.AddressFamily));
        var type = source.AddressFamily == AddressFamily.InterNetworkV6 ? Dns.DnsType.AAAA : Dns.DnsType.A;
        foreach (var server in servers.Distinct())
        {
            try
            {
                var r = await Dns.MiniDnsClient.QueryAsync(server, host, type, 2500, ct, source);
                var real = r.Addresses.Where(a => !IsFakeIp(a)).ToArray();
                if (real.Length > 0) return real;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { }
        }
        throw new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>Fake-IP range used by proxy clients for DNS interception (RFC 2544 benchmarking block).</summary>
    public static bool IsFakeIp(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 198 && b[1] is 18 or 19;
    }
}

/// <summary>Tiny persistent cache: key → (fetchedAt, json value). Saved with a debounce.</summary>
public sealed class DiskCache
{
    private readonly string _path;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, CacheEntry> _items = new();
    private int _saveScheduled;

    public sealed record CacheEntry(long FetchedAt, JsonNode? Value);

    public DiskCache(string path, TimeSpan ttl)
    {
        _path = path;
        _ttl = ttl;
        try
        {
            if (File.Exists(path))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(path), Logging.Json.Options);
                if (d != null)
                    foreach (var (k, v) in d)
                        if (Fresh(v)) _items[k] = v;
            }
        }
        catch { }
    }

    private bool Fresh(CacheEntry e) => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - e.FetchedAt < _ttl.TotalMilliseconds;

    public bool TryGet(string key, out CacheEntry entry)
    {
        if (_items.TryGetValue(key, out entry!) && Fresh(entry)) return true;
        entry = null!;
        return false;
    }

    public void Set(string key, JsonNode? value)
    {
        _items[key] = new CacheEntry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), value);
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1) return;
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _saveScheduled, 0);
            try
            {
                var snapshot = _items.Where(kv => Fresh(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
                File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, Logging.Json.Options));
            }
            catch { }
        });
    }
}

public sealed class CloudflareMeta
{
    public string? ClientIp { get; init; }
    public int? Asn { get; init; }
    public string? AsOrganization { get; init; }
    public string? Country { get; init; }
    public string? City { get; init; }
    public string? Region { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? ColoIata { get; init; }
    public string? ColoCity { get; init; }
    public double? ColoLat { get; init; }
    public double? ColoLon { get; init; }
    public long FetchedAt { get; init; }

    public GeoDto? ToGeo() => Latitude is { } lat && Longitude is { } lon
        ? new GeoDto(lat, lon, City, Region, null, Country, "cloudflare", City != null ? "city" : "country")
        : null;
}

/// <summary>
/// Cloudflare's own view of a connection (speed.cloudflare.com/meta): client IP, ASN, location and the
/// Cloudflare data centre that answered. The same geolocation Cloudflare uses for its services.
/// </summary>
public static class CloudflareMetaClient
{
    public static async Task<CloudflareMeta?> FetchAsync(IPAddress? source, CancellationToken ct)
    {
        try
        {
            using var http = BoundHttp.Create(source, TimeSpan.FromSeconds(8));
            var json = await http.GetStringAsync("https://speed.cloudflare.com/meta", ct);
            return Parse(json);
        }
        catch
        {
            return null;
        }
    }

    internal static CloudflareMeta? Parse(string json)
    {
        var n = JsonNode.Parse(json);
        if (n is not JsonObject o) return null;
        var colo = o["colo"];
        string? iata = null, coloCity = null;
        double? cLat = null, cLon = null;
        if (colo is JsonObject co)
        {
            iata = Str(co["iata"]);
            coloCity = Str(co["city"]);
            cLat = Num(co["lat"]);
            cLon = Num(co["lon"]);
        }
        else if (colo != null)
        {
            iata = Str(colo);
        }
        return new CloudflareMeta
        {
            ClientIp = Str(o["clientIp"]),
            Asn = (int?)Num(o["asn"]),
            AsOrganization = Str(o["asOrganization"]),
            Country = Str(o["country"]),
            City = Str(o["city"]),
            Region = Str(o["region"]),
            Latitude = Num(o["latitude"]),
            Longitude = Num(o["longitude"]),
            ColoIata = iata,
            ColoCity = coloCity,
            ColoLat = cLat,
            ColoLon = cLon,
            FetchedAt = GeoCandidate.Now,
        };
    }

    /// <summary>Data centre that serves a Cloudflare-fronted host: /cdn-cgi/trace returns "colo=FRA".</summary>
    public static async Task<string?> TraceColoAsync(string host, IPAddress? source, CancellationToken ct)
    {
        try
        {
            using var http = BoundHttp.Create(source, TimeSpan.FromSeconds(6));
            var text = await http.GetStringAsync($"https://{host}/cdn-cgi/trace", ct);
            foreach (var line in text.Split('\n'))
                if (line.StartsWith("colo=", StringComparison.Ordinal)) return line[5..].Trim();
        }
        catch { }
        return null;
    }

    internal static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>Numbers that may arrive as JSON numbers or strings ("40.85843").</summary>
    internal static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }
}

/// <summary>
/// RIPE IPmap: geolocation built for network infrastructure from RIPE Atlas latency measurements,
/// crowdsourcing and reverse DNS. Parsed tolerantly: any object carrying latitude/longitude is a location.
/// </summary>
public sealed class RipeIpmapClient(DiskCache cache)
{
    private readonly SemaphoreSlim _gate = new(3);
    private readonly HttpClient _http = BoundHttp.Create(null, TimeSpan.FromSeconds(12));

    public async Task<(GeoCandidate? Geo, bool Anycast)> LocateAsync(IPAddress ip, CancellationToken ct)
    {
        var key = ip.ToString();
        JsonNode? node;
        long fetched;
        if (cache.TryGet(key, out var hit))
        {
            node = hit.Value;
            fetched = hit.FetchedAt;
        }
        else
        {
            await _gate.WaitAsync(ct);
            try
            {
                var text = await _http.GetStringAsync($"https://ipmap-api.ripe.net/v1/locate/{key}/best?client=mtrtracer", ct);
                node = JsonNode.Parse(text);
                fetched = GeoCandidate.Now;
                cache.Set(key, node);
            }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                cache.Set(key, null);
                return (null, false);
            }
            finally
            {
                _gate.Release();
            }
        }
        return Parse(node, fetched);
    }

    internal static (GeoCandidate? Geo, bool Anycast) Parse(JsonNode? node, long fetched)
    {
        if (node == null) return (null, false);
        var anycast = FindBool(node, "anycast");
        var loc = FindLocation(node);
        if (loc == null) return (null, anycast);
        var lat = CloudflareMetaClient.Num(loc["latitude"]);
        var lon = CloudflareMetaClient.Num(loc["longitude"]);
        if (lat == null || lon == null) return (null, anycast);
        var city = CloudflareMetaClient.Str(loc["cityNameAscii"]) ?? CloudflareMetaClient.Str(loc["cityName"]);
        var level = CloudflareMetaClient.Str(loc["type"]) == "country" || city == null ? "country" : "city";
        var score = CloudflareMetaClient.Num(loc["score"]);
        return (new GeoCandidate(GeoSources.RipeIpmap, lat.Value, lon.Value, city, CloudflareMetaClient.Str(loc["stateName"]),
            CloudflareMetaClient.Str(loc["countryCodeAlpha2"]), level, fetched, score != null ? $"score {score:0.##}" : null), anycast);
    }

    private static JsonObject? FindLocation(JsonNode node, int depth = 0)
    {
        if (depth > 6) return null;
        if (node is JsonObject o)
        {
            if (o.ContainsKey("latitude") && o.ContainsKey("longitude") && o["latitude"] != null) return o;
            foreach (var key in new[] { "location", "locations", "result", "data" })
                if (o[key] is { } child && FindLocation(child, depth + 1) is { } found) return found;
            foreach (var (_, v) in o)
                if (v != null && FindLocation(v, depth + 1) is { } found) return found;
        }
        else if (node is JsonArray a)
        {
            foreach (var item in a)
                if (item != null && FindLocation(item, depth + 1) is { } found) return found;
        }
        return null;
    }

    private static bool FindBool(JsonNode node, string name, int depth = 0)
    {
        if (depth > 6) return false;
        if (node is JsonObject o)
        {
            foreach (var (k, v) in o)
            {
                if (k == name && v is JsonValue jv && jv.TryGetValue<bool>(out var b) && b) return true;
                if (v != null && FindBool(v, name, depth + 1)) return true;
            }
        }
        else if (node is JsonArray a)
        {
            foreach (var item in a)
                if (item != null && FindBool(item, name, depth + 1)) return true;
        }
        return false;
    }
}

public sealed record NetworkInfo(int? Asn, string? Prefix, string? Holder);

/// <summary>
/// RIPEstat data API (no key): current MaxMind GeoLite2 location (maxmind-geo-lite), and the BGP origin AS and
/// prefix (network-info) with the AS holder name (as-overview).
/// </summary>
public sealed class RipeStatClient(DiskCache geoCache, DiskCache netCache)
{
    private readonly SemaphoreSlim _gate = new(4);
    private readonly HttpClient _http = BoundHttp.Create(null, TimeSpan.FromSeconds(12));

    private async Task<JsonNode?> GetAsync(string call, string resource, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var text = await _http.GetStringAsync($"https://stat.ripe.net/data/{call}/data.json?resource={Uri.EscapeDataString(resource)}&sourceapp=mtrtracer", ct);
            var n = JsonNode.Parse(text);
            return n?["status"]?.GetValue<string>() == "ok" ? n["data"] : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GeoCandidate?> MaxMindAsync(IPAddress ip, CancellationToken ct)
    {
        var key = ip.ToString();
        JsonNode? data;
        long fetched;
        if (geoCache.TryGet(key, out var hit))
        {
            data = hit.Value;
            fetched = hit.FetchedAt;
        }
        else
        {
            data = await GetAsync("maxmind-geo-lite", key, ct);
            fetched = GeoCandidate.Now;
            geoCache.Set(key, data);
        }
        return ParseMaxMind(data, fetched);
    }

    internal static GeoCandidate? ParseMaxMind(JsonNode? data, long fetched)
    {
        // data.located_resources[].locations[] { country, city, latitude, longitude, covered_percentage }
        if (data?["located_resources"] is not JsonArray res) return null;
        JsonObject? best = null;
        double bestCover = -1;
        foreach (var r in res)
        {
            if (r?["locations"] is not JsonArray locs) continue;
            foreach (var l in locs.OfType<JsonObject>())
            {
                var cover = CloudflareMetaClient.Num(l["covered_percentage"]) ?? 0;
                var hasCity = CloudflareMetaClient.Str(l["city"]) != null;
                var rank = cover + (hasCity ? 1000 : 0);
                if (rank > bestCover && CloudflareMetaClient.Num(l["latitude"]) != null)
                {
                    best = l;
                    bestCover = rank;
                }
            }
        }
        if (best == null) return null;
        var city = CloudflareMetaClient.Str(best["city"]);
        return new GeoCandidate(GeoSources.MaxMind, CloudflareMetaClient.Num(best["latitude"])!.Value, CloudflareMetaClient.Num(best["longitude"])!.Value,
            city, null, CloudflareMetaClient.Str(best["country"]), city != null ? "city" : "country", fetched);
    }

    public async Task<NetworkInfo?> NetworkAsync(IPAddress ip, CancellationToken ct)
    {
        var key = "net:" + ip;
        if (netCache.TryGet(key, out var hit)) return hit.Value?.Deserialize<NetworkInfo>(Logging.Json.Options);
        var data = await GetAsync("network-info", ip.ToString(), ct);
        int? asn = null;
        if (data?["asns"] is JsonArray asns && asns.Count > 0)
        {
            var first = asns[0];
            if (first is JsonValue v && (v.TryGetValue<string>(out var s) && int.TryParse(s, out var a) || v.TryGetValue<int>(out a))) asn = a;
        }
        var prefix = CloudflareMetaClient.Str(data?["prefix"]);
        string? holder = null;
        if (asn != null)
        {
            var hkey = $"as:{asn}";
            if (netCache.TryGet(hkey, out var h)) holder = h.Value?.GetValue<string>();
            else
            {
                var ov = await GetAsync("as-overview", $"AS{asn}", ct);
                holder = CloudflareMetaClient.Str(ov?["holder"]);
                netCache.Set(hkey, holder == null ? null : JsonValue.Create(holder));
            }
        }
        var info = new NetworkInfo(asn, prefix, holder);
        netCache.Set(key, JsonSerializer.SerializeToNode(info, Logging.Json.Options));
        return info;
    }
}
