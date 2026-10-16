using System.Net;
using MTRTracer.Core.Adapters;
using MTRTracer.Core.Geo;
using MTRTracer.Core.Probing;
using MTRTracer.Core.Protocol;

namespace MTRTracer.Core.Mtr;

public sealed record VpnExitDto(string? Ip, string? AsOrg, int? Asn, string? City, string? Country, double? Lat, double? Lon, string? Colo, string? ColoCity, double? ColoLat, double? ColoLon, long FetchedAt);

public sealed record VpnBlockDto(
    string Server,
    int ServerPort,
    string? Process,
    string? Tunnel,
    string? Carrier,
    VpnExitDto? Exit,
    string Target,
    string? TargetIp,
    int Port,
    string Method,
    int Sent,
    int Recv,
    double Loss,
    double? Last,
    double? Best,
    double? Avg,
    double? Worst,
    double? JAvg,
    double? SetupMs,
    bool ConnectFaked,
    double? ServerBest,
    double? LegMs,
    string Status,
    double?[] History,
    HopGeoDto? TargetGeo);

/// <summary>
/// VPN mode. The underlay <see cref="MtrSession"/> traces the physical path to the VPN server (real hops);
/// this overlay measures the real end-to-end latency to the destination through the tunnel and works out
/// the server → destination leg. Its block is attached to every snapshot of the underlay session.
/// </summary>
public sealed class VpnOverlay : IAsyncDisposable
{
    private readonly MtrSession _underlay;
    private readonly string _input;
    private readonly TunnelInfo _tunnel;
    private readonly TunnelServerCandidate _server;
    private readonly HostInfoService _hosts;
    private readonly TraceOptions _opts;
    private readonly LatencyStats _stats = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private TunnelProbe? _probe;
    private VpnExitDto? _exit;
    private IPAddress? _targetIp;
    private string _host = "";
    private int _port = 443;
    private double? _setup;
    private bool _faked;
    private string _status = Loc.S("запуск", "starting");

    public VpnOverlay(MtrSession underlay, string input, TunnelInfo tunnel, TunnelServerCandidate server, HostInfoService hosts, TraceOptions opts)
    {
        _underlay = underlay;
        _input = input;
        _tunnel = tunnel;
        _server = server;
        _hosts = hosts;
        _opts = opts;
        underlay.SnapshotExtra = Block;
        underlay.State.Mode = "vpn";
        underlay.State.OverlayInput = input;
        underlay.State.CarrierName = tunnel.CarrierName;
    }

    /// <summary>Source address for tunnel traffic: none when the tunnel is the default route, else the tunnel's own IP.</summary>
    private IPAddress? TunnelSource()
    {
        if (_tunnel.IsDefaultRoute) return null;
        var ni = AdapterService.Find(_tunnel.AdapterId);
        return ni?.GetIPProperties().UnicastAddresses.Select(u => u.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
    }

    public void Start() => _loop = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        var parsed = TargetParser.Parse(_input);
        if (parsed == null)
        {
            _status = Loc.S("не удалось распознать адрес", "could not parse the address");
            return;
        }
        _host = parsed.Host;
        _port = parsed.Port ?? 443;
        try
        {
            // System DNS: with a proxy tunnel this resolves the way the browser would (often through the tunnel).
            var addrs = IPAddress.TryParse(_host, out var lit) ? [lit] : await System.Net.Dns.GetHostAddressesAsync(_host, ct);
            _targetIp = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
        }
        catch
        {
            _status = Loc.S("DNS не ответил", "DNS did not answer");
        }
        if (_targetIp == null) return;
        _underlay.State.OverlayIp = AdapterService.Format(_targetIp);
        _hosts.Discover(_targetIp);

        var source = TunnelSource();
        _ = Task.Run(async () =>
        {
            var meta = await CloudflareMetaClient.FetchAsync(source, ct);
            if (meta != null)
                _exit = new VpnExitDto(meta.ClientIp, meta.AsOrganization, meta.Asn, meta.City, meta.Country, meta.Latitude, meta.Longitude,
                    meta.ColoIata, meta.ColoCity, meta.ColoLat, meta.ColoLon, meta.FetchedAt);
            if (_host != AdapterService.Format(_targetIp)) _hosts.CheckCloudflareColo(_targetIp, _host, source);
        }, ct);

        _probe = new TunnelProbe(_host, _targetIp, _port, source, _opts.TimeoutMs);
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            if (_underlay.State.State is "paused" or "waiting-network")
            {
                try { await Task.Delay(500, ct); } catch { break; }
                continue;
            }
            var s = await _probe.ProbeAsync(ct);
            lock (_stats)
            {
                _stats.AddSent();
                if (s.Ok) _stats.AddReply(s.RttMs); else _stats.AddLoss();
            }
            if (s.SetupMs != null) _setup = s.SetupMs;
            _faked |= s.ConnectFaked;
            _status = s.Status;
            if (!s.Ok && _stats.Recv == 0 && ++failures >= 2 && _port == 443 && parsed.Port == null)
            {
                // Nothing on 443: fall back to plain HTTP.
                await _probe.DisposeAsync();
                _port = 80;
                _probe = new TunnelProbe(_host, _targetIp, _port, source, _opts.TimeoutMs);
                failures = 0;
            }
            var wait = _opts.IntervalMs - (Environment.TickCount64 - started);
            if (wait > 0)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(wait), ct); } catch { break; }
            }
        }
    }

    public object Block()
    {
        double? serverBest = _underlay.DestinationBest();
        LatencyStats s = _stats;
        VpnBlockDto dto;
        lock (_stats)
        {
            double? best = Num.N(s.Best);
            double? leg = best != null && serverBest != null ? Math.Max(0, best.Value - serverBest.Value) : null;
            dto = new VpnBlockDto(_server.Ip, _server.Port, _tunnel.Process, _tunnel.AdapterName, _tunnel.CarrierName, _exit,
                _host.Length > 0 ? _host : _input, _targetIp == null ? null : AdapterService.Format(_targetIp), _port, _port == 443 ? "https" : "http",
                s.Sent, s.Recv, Math.Round(s.LossPct, 1), Num.N(s.Last), best, Num.N(s.Avg), Num.N(s.Worst), Num.N(s.JAvg),
                _setup == null ? null : Math.Round(_setup.Value, 2), _faked, serverBest, leg == null ? null : Math.Round(leg.Value, 2), _status,
                s.History().Select(v => Num.N(v, 2)).ToArray(), PlaceTarget(best));
        }
        return dto;
    }

    /// <summary>Destination location checked against the physical bound home → VPN server → destination.</summary>
    private HopGeoDto? PlaceTarget(double? overlayBest)
    {
        if (_targetIp == null) return null;
        var info = _hosts.Get(_targetIp.ToString());
        var geo = _underlay.LastGeo;
        var origin = geo?.Origin;
        var server = geo?.Hops.LastOrDefault(h => h.Lat != null && h.Status is "confirmed" or "single" or "anycast" or "unresolved");
        var verdicts = new List<CandidateVerdict>();
        GeoCandidate? chosen = null;
        foreach (var c in info?.Candidates.OrderByDescending(x => GeoSources.Weight(x.Source)) ?? Enumerable.Empty<GeoCandidate>())
        {
            string? reason = null;
            if (origin != null && overlayBest != null)
            {
                var path = server != null
                    ? RouteGeoSolver.Haversine(origin.Lat, origin.Lon, server.Lat!.Value, server.Lon!.Value) + RouteGeoSolver.Haversine(server.Lat.Value, server.Lon!.Value, c.Lat, c.Lon)
                    : RouteGeoSolver.Haversine(origin.Lat, origin.Lon, c.Lat, c.Lon);
                var allowed = overlayBest.Value * RouteGeoSolver.KmPerMsRtt + RouteGeoSolver.DetourSlackKm;
                if (path > allowed) reason = Loc.S($"путь через сервер VPN {Math.Round(path):0} км, а сквозной RTT {overlayBest:0.0} мс допускает {Math.Round(allowed):0} км", $"the path via the VPN server is {Math.Round(path):0} km, but the end-to-end RTT of {overlayBest:0.0} ms allows {Math.Round(allowed):0} km");
            }
            if (reason == null && chosen == null) chosen = c;
            verdicts.Add(new CandidateVerdict(c.Source, c.Lat, c.Lon, c.City, c.CountryCode, c.Level, c.FetchedAt, reason == null, reason ?? (ReferenceEquals(c, chosen) ? Loc.S("выбран", "chosen") : null)));
        }
        var ip = AdapterService.Format(_targetIp);
        if (chosen != null)
            return new HopGeoDto(0, ip, chosen.Lat, chosen.Lon, "single", chosen.Source, chosen.City, chosen.CountryCode,
                GeoSources.Label(chosen.Source), verdicts, null);
        var anycast = info != null && (info.Anycast || info.Asn is { } asn && RouteGeoSolver.AnycastAsns.Contains(asn));
        if (anycast && (server != null || _exit?.ColoLat != null))
        {
            var lat = _exit?.ColoLat ?? server!.Lat;
            var lon = _exit?.ColoLon ?? server!.Lon;
            return new HopGeoDto(0, ip, lat, lon, "anycast", null, _exit?.ColoCity, null,
                Loc.S("anycast: отвечает узел рядом с выходом VPN", "anycast: the node near the VPN exit answers"), verdicts, null);
        }
        if (info is { Candidates.Count: > 0 })
        {
            var f = RouteGeoSolver.BestUnchecked(info.Candidates);
            return new HopGeoDto(0, ip, f.Lat, f.Lon, "unresolved", f.Source, f.City, f.CountryCode,
                Loc.S($"{GeoSources.Label(f.Source)}: место не сходится с измеренной задержкой, показано как есть", $"{GeoSources.Label(f.Source)}: the location does not match the measured latency, shown as is"), verdicts, null);
        }
        return new HopGeoDto(0, ip, null, null, info == null || info.Pending > 0 ? "pending" : "unresolved", null, null, null,
            Loc.S("нет данных о местоположении", "no location data"), verdicts, null);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
        if (_probe != null) await _probe.DisposeAsync();
        _cts.Dispose();
    }
}
