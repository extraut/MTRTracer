using System.Collections.Concurrent;
using System.Net;
using MTRTracer.Core.Dns;
using MTRTracer.Core.Protocol;

namespace MTRTracer.Core.Geo;

/// <summary>
/// Collects everything known about an address, from several independent sources, each tagged with its
/// origin and fetch time: offline DB-IP, reverse DNS + router-name hints, RIPE IPmap, MaxMind GeoLite via
/// RIPEstat, BGP origin AS (RIPEstat, Team Cymru), ip-api for ISP names. Choosing the final location is
/// left to <see cref="RouteGeoSolver"/>, which has the RTT context. Emits <c>host.info</c> as data arrives.
/// </summary>
public sealed class HostInfoService
{
    private readonly ResolverService _resolver;
    private readonly DbIpDatabase _db;
    private readonly IpApiClient _ipApi;
    private readonly RipeIpmapClient _ipmap;
    private readonly RipeStatClient _ripeStat;
    private readonly IEventSink _sink;
    private readonly DiskCache _ipmapCache;
    private readonly DiskCache _geoCache;
    private readonly DiskCache _netCache;
    private readonly ConcurrentDictionary<string, HostInfoDto> _infos = new();
    private readonly ConcurrentDictionary<string, byte> _coloChecked = new();

    public bool ResolveNames { get; set; } = true;
    public bool Online { get; set; } = true;
    public GeoProviders Providers { get; set; } = new();

    public event Action<string>? Updated;

    public HostInfoService(ResolverService resolver, DbIpDatabase db, IpApiClient ipApi, string cacheDir, IEventSink sink)
    {
        _resolver = resolver;
        _db = db;
        _ipApi = ipApi;
        _ipmapCache = new DiskCache(Path.Combine(cacheDir, "ripe-ipmap.json"), TimeSpan.FromDays(7));
        _geoCache = new DiskCache(Path.Combine(cacheDir, "ripestat-geo.json"), TimeSpan.FromDays(7));
        _netCache = new DiskCache(Path.Combine(cacheDir, "ripestat-net.json"), TimeSpan.FromDays(2));
        _ipmap = new RipeIpmapClient(_ipmapCache);
        _ripeStat = new RipeStatClient(_geoCache, _netCache);
        _sink = sink;
    }

    public HostInfoDto? Get(string ip) => _infos.TryGetValue(ip, out var i) ? i : null;

    public void Discover(IPAddress ip, bool full = true)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var key = ip.ToString();
        var created = false;
        var info = _infos.GetOrAdd(key, _ =>
        {
            created = true;
            return new HostInfoDto { Ip = key, Kind = IpClassifier.Classify(ip) };
        });
        if (!created) return;
        _ = Task.Run(() => EnrichAsync(ip, info, full));
    }

    /// <summary>Offline-only info for the connections table (no network requests per connection).</summary>
    public HostInfoDto Quick(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var key = ip.ToString();
        if (_infos.TryGetValue(key, out var existing)) return existing;
        var info = new HostInfoDto { Ip = key, Kind = IpClassifier.Classify(ip) };
        if (info.Kind == "public" && Providers.OfflineDb) ApplyDb(ip, info);
        return info;
    }

    private void Emit(HostInfoDto info)
    {
        HostInfoDto copy;
        lock (info) copy = info.Clone();
        _sink.Emit("host.info", copy);
        Updated?.Invoke(info.Ip);
    }

    private void ApplyDb(IPAddress ip, HostInfoDto info)
    {
        lock (info)
        {
            if (_db.LookupCity(ip) is { } g)
                info.AddCandidate(new GeoCandidate(GeoSources.DbIp, g.Lat, g.Lon, g.City, g.Region, g.CountryCode, g.Accuracy, GeoCandidate.Now,
                    _db.DbDate != null ? Loc.S($"выпуск {_db.DbDate}", $"release {_db.DbDate}") : null));
            if (info.Asn == null && _db.LookupAsn(ip) is { } asn)
            {
                info.Asn = asn.Asn;
                info.AsName = asn.Org;
                info.AsSource = "DB-IP ASN Lite";
            }
        }
    }

    private async Task Step(HostInfoDto info, Func<Task> work)
    {
        lock (info) info.Pending++;
        try
        {
            await work();
        }
        catch
        {
            // Each source is best effort; the others still count.
        }
        finally
        {
            lock (info) info.Pending--;
            Emit(info);
        }
    }

    private async Task EnrichAsync(IPAddress ip, HostInfoDto info, bool full)
    {
        var isPublic = info.Kind == "public";
        if (isPublic && Providers.OfflineDb) ApplyDb(ip, info);
        Emit(info);
        if (!full) return;

        var tasks = new List<Task>();
        if (ResolveNames)
        {
            tasks.Add(Step(info, async () =>
            {
                var name = await _resolver.ReverseAsync(ip);
                lock (info)
                {
                    info.Hostname = name;
                    info.HostnameDone = true;
                    if (isPublic && Providers.HostnameHints && HostnameHints.Match(name) is { } hint)
                    {
                        info.HintToken = hint.Token;
                        info.AddCandidate(new GeoCandidate(GeoSources.Hint, hint.Geo.Lat, hint.Geo.Lon, hint.Geo.City, null, hint.Geo.CountryCode,
                            "city", GeoCandidate.Now, Loc.S($"«{hint.Token}» в {name}", $"\"{hint.Token}\" in {name}")));
                    }
                }
            }));
        }
        if (!isPublic)
        {
            await Task.WhenAll(tasks);
            return;
        }

        if (Online)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var pv = Providers;
            if (pv.RipeIpmap) tasks.Add(Step(info, async () =>
            {
                var (geo, anycast) = await _ipmap.LocateAsync(ip, cts.Token);
                lock (info)
                {
                    info.AddCandidate(geo);
                    info.Anycast |= anycast;
                }
            }));
            if (pv.RipeStat) tasks.Add(Step(info, async () =>
            {
                var geo = await _ripeStat.MaxMindAsync(ip, cts.Token);
                lock (info) info.AddCandidate(geo);
            }));
            if (pv.RipeStat) tasks.Add(Step(info, async () =>
            {
                var net = await _ripeStat.NetworkAsync(ip, cts.Token);
                if (net?.Asn == null) return;
                lock (info)
                {
                    // Live BGP data beats the monthly offline ASN table.
                    info.Asn = net.Asn;
                    info.AsName = net.Holder ?? info.AsName;
                    info.Prefix = net.Prefix ?? info.Prefix;
                    info.AsSource = "RIPEstat (BGP)";
                }
            }));
            if (pv.IpApi) tasks.Add(Step(info, async () =>
            {
                var rec = await _ipApi.LookupAsync(ip);
                if (rec?.Status != "success") return;
                lock (info)
                {
                    info.Isp = rec.Isp;
                    info.Org = rec.Org;
                    if (rec.ToGeo() is { } g)
                        info.AddCandidate(new GeoCandidate(GeoSources.IpApi, g.Lat, g.Lon, g.City, g.Region, g.CountryCode, g.Accuracy,
                            new DateTimeOffset(rec.Fetched).ToUnixTimeMilliseconds()));
                }
            }));
            await Task.WhenAll(tasks);
        }
        else
        {
            await Task.WhenAll(tasks);
        }

        if (info.Asn == null && Providers.TeamCymru)
        {
            await Step(info, async () =>
            {
                var asn = await _resolver.AsnAsync(ip);
                if (asn == null) return;
                lock (info)
                {
                    info.Asn = asn.Asn;
                    info.AsName ??= asn.Name;
                    info.Prefix ??= asn.Prefix;
                    info.Registry = asn.Registry;
                    info.AsSource = "Team Cymru (BGP)";
                }
            });
        }
    }

    /// <summary>
    /// For a destination behind Cloudflare: ask the edge which data centre answered (/cdn-cgi/trace colo=XXX).
    /// That is the exact location of an anycast address, as seen from this connection.
    /// </summary>
    public void CheckCloudflareColo(IPAddress ip, string host, IPAddress? source)
    {
        var info = Get(ip.ToString());
        if (info == null || !Online || !Providers.CloudflareColo || info.Asn != 13335 || !_coloChecked.TryAdd(ip + "|" + host, 0)) return;
        _ = Step(info, async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var colo = await CloudflareMetaClient.TraceColoAsync(host, source, cts.Token);
            if (HostnameHints.ByCode(colo) is not { } g) return;
            lock (info)
            {
                info.Anycast = true;
                info.AddCandidate(new GeoCandidate(GeoSources.CloudflareColo, g.Lat, g.Lon, g.City, null, g.CountryCode, "city", GeoCandidate.Now,
                    Loc.S($"ответил дата-центр Cloudflare {colo}", $"answered by Cloudflare data centre {colo}")));
            }
        });
    }

    public object CacheStats() => new { hosts = _infos.Count, ripeIpmap = _ipmapCache.Count, ripeStatGeo = _geoCache.Count, ripeStatNet = _netCache.Count, ipApi = _ipApi.CacheCount };

    /// <summary>Forgets everything fetched from online sources (memory and disk); the next lookups ask again.</summary>
    public void ClearCaches()
    {
        _ipmapCache.Clear();
        _geoCache.Clear();
        _netCache.Clear();
        _ipApi.ClearCache();
        var known = _infos.Keys.ToList();
        _infos.Clear();
        _coloChecked.Clear();
        // Addresses of the current route are looked up again right away.
        foreach (var k in known)
            if (IPAddress.TryParse(k, out var ip)) Discover(ip);
    }

    /// <summary>Called when the offline DB becomes available: fill in what was missing.</summary>
    public void RefreshFromDb()
    {
        if (!Providers.OfflineDb) return;
        foreach (var info in _infos.Values.Where(i => i.Kind == "public"))
        {
            if (!IPAddress.TryParse(info.Ip, out var ip)) continue;
            var had = info.Candidates.Any(c => c.Source == GeoSources.DbIp);
            ApplyDb(ip, info);
            if (!had && info.Candidates.Any(c => c.Source == GeoSources.DbIp)) Emit(info);
        }
    }
}

