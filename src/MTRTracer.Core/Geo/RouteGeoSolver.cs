namespace MTRTracer.Core.Geo;

public sealed record GeoPoint(double Lat, double Lon, string? Label, string Source);

public sealed record HopGeoInput(int Ttl, string? Ip, double? Best, int Recv, HostInfoDto? Info, bool IsDestination);

public sealed record CandidateVerdict(string Source, double Lat, double Lon, string? City, string? CountryCode, string Level, long FetchedAt, bool Accepted, string? Reason);

public sealed record HopGeoDto(
    int Ttl,
    string? Ip,
    double? Lat,
    double? Lon,
    string Status,
    string? Source,
    string? City,
    string? CountryCode,
    string? Reason,
    List<CandidateVerdict> Verdicts,
    double? MaxDistanceKm);

public sealed record RouteSummaryDto(double PathKm, double? DirectKm, double? MinRttMs, double? MeasuredMs, double? Stretch, int Unresolved);

public sealed record RouteGeoDto(string SessionId, GeoPoint? Origin, List<HopGeoDto> Hops, RouteSummaryDto Summary, string? OriginNote = null);

/// <summary>
/// Decides where each hop goes on the map. Every candidate location must be physically reachable:
/// light in fibre covers ~100 km per millisecond of round-trip time, so
///  • radius:  d(origin, hop_k) ≤ 100·min(best RTT of hop_k and every later hop) + slack
///            (packets to later hops pass through hop_k, so their RTT also bounds it);
///  • detour:  d(origin, prev) + d(prev, hop_k) ≤ 100·RTT_k + slack
///            (the path to hop_k really goes through the previously placed hop).
/// Among the candidates that pass, trusted sources and agreement between sources win. Nothing that fails
/// is drawn; anycast addresses are pinned to the nearest confirmed point instead of their registry address.
/// </summary>
public static class RouteGeoSolver
{
    public const double KmPerMsRtt = 100.0;
    public const double RadiusSlackKm = 60;
    public const double DetourSlackKm = 180;
    public const double AgreeKm = 120;

    /// <summary>Networks known to announce the same addresses from many places.</summary>
    public static readonly HashSet<int> AnycastAsns = [13335, 15169, 16509, 8075, 20940, 54113, 19281, 36692, 42, 8068, 396982, 209242, 2906];

    public static double MaxDistance(double rttMs) => rttMs * KmPerMsRtt + RadiusSlackKm;

    public static RouteGeoDto Solve(string sessionId, GeoPoint? origin, IReadOnlyList<HopGeoInput> hops)
    {
        // Effective RTT bound per hop: the minimum best RTT of this and every later responding hop.
        var eff = new double?[hops.Count];
        double? running = null;
        for (var i = hops.Count - 1; i >= 0; i--)
        {
            var h = hops[i];
            if (h.Best is { } b && h.Recv >= 2) running = running == null ? b : Math.Min(running.Value, b);
            eff[i] = running;
        }

        // Without a known origin, anchor on the first public hop with a trusted city-level candidate.
        var o = origin;
        if (o == null)
        {
            foreach (var h in hops)
            {
                var c = h.Info?.Candidates.Where(x => x.Level == "city").OrderByDescending(x => GeoSources.Weight(x.Source)).FirstOrDefault();
                if (h.Info?.Kind == "public" && c != null && GeoSources.Weight(c.Source) >= 5)
                {
                    o = new GeoPoint(c.Lat, c.Lon, c.City, "first-hop");
                    break;
                }
            }
        }

        // Sanity check of the origin itself: the first nearby public router (low RTT) whose location is
        // well established must lie within reach of the origin. If not, the origin is wrong (typically the
        // VPN exit instead of the user's place) and the router becomes the anchor instead.
        string? originNote = null;
        if (o != null)
        {
            for (var i = 0; i < hops.Count; i++)
            {
                var h = hops[i];
                if (h.Info?.Kind != "public" || eff[i] is not { } rtt) continue;
                if (rtt > 30) break;
                var anchor = Established(h.Info);
                if (anchor == null) continue;
                var d = Haversine(o.Lat, o.Lon, anchor.Lat, anchor.Lon);
                var lim = MaxDistance(rtt);
                if (d > lim)
                {
                    var where = anchor.City ?? $"{anchor.Lat:0.0}, {anchor.Lon:0.0}";
                    if (o.Source == "manual")
                    {
                        originNote = $"Указанное вручную место ({o.Label}) в {Math.Round(d):0} км от узла провайдера {h.Ip} ({where}), а он отвечает за {rtt:0.0} мс: так далеко быть не может. Проверьте город во вкладке «Сведения».";
                    }
                    else
                    {
                        originNote = $"Ваше место по данным {(o.Source == "cloudflare" ? "Cloudflare" : "IP")} ({o.Label}) не сходится с задержкой: узел провайдера {h.Ip} ({where}) отвечает за {rtt:0.0} мс, значит вы не дальше {Math.Round(lim):0} км от него. Похоже, запрос ушёл через VPN. Отсчёт ведётся от {where}.";
                        o = new GeoPoint(anchor.Lat, anchor.Lon, anchor.City, "first-hop");
                    }
                }
                break;
            }
        }

        var result = new List<HopGeoDto>(hops.Count);
        GeoPoint? prev = null; // last placed public hop
        for (var i = 0; i < hops.Count; i++)
        {
            var h = hops[i];
            var limit = eff[i] is { } e ? MaxDistance(e) : (double?)null;

            if (h.Ip == null)
            {
                result.Add(new HopGeoDto(h.Ttl, null, null, null, "silent", null, null, null, null, [], limit));
                continue;
            }
            if (h.Info == null)
            {
                result.Add(new HopGeoDto(h.Ttl, h.Ip, null, null, "pending", null, null, null, null, [], limit));
                continue;
            }
            if (h.Info.Kind != "public")
            {
                result.Add(new HopGeoDto(h.Ttl, h.Ip, o?.Lat, o?.Lon, "local", null, null, null, "адрес локальной сети или провайдера, место совпадает с вашим", [], limit));
                continue;
            }

            var verdicts = new List<CandidateVerdict>();
            var passing = new List<GeoCandidate>();
            foreach (var c in h.Info.Candidates)
            {
                string? reason = null;
                if (o != null && limit is { } lim)
                {
                    var d = Haversine(o.Lat, o.Lon, c.Lat, c.Lon);
                    if (d > lim)
                        reason = $"{Math.Round(d):0} км от вас, а RTT {eff[i]:0.0} мс допускает не больше {Math.Round(lim):0} км";
                }
                if (reason == null && o != null && prev != null && eff[i] is { } rtt)
                {
                    var detour = Haversine(o.Lat, o.Lon, prev.Lat, prev.Lon) + Haversine(prev.Lat, prev.Lon, c.Lat, c.Lon);
                    var allowed = rtt * KmPerMsRtt + DetourSlackKm;
                    if (detour > allowed)
                        reason = $"путь через предыдущий узел ({prev.Label}) {Math.Round(detour):0} км, а RTT {rtt:0.0} мс допускает {Math.Round(allowed):0} км";
                }
                if (reason == null) passing.Add(c);
                verdicts.Add(new CandidateVerdict(c.Source, c.Lat, c.Lon, c.City, c.CountryCode, c.Level, c.FetchedAt, reason == null, reason));
            }

            if (passing.Count > 0)
            {
                GeoCandidate best = passing[0];
                double bestScore = double.MinValue;
                int bestAgree = 0;
                foreach (var c in passing)
                {
                    var agree = passing.Count(x => !ReferenceEquals(x, c) && x.Source != c.Source && Haversine(x.Lat, x.Lon, c.Lat, c.Lon) <= AgreeKm);
                    var score = GeoSources.Weight(c.Source) + agree * 1.5 - (c.Level == "city" ? 0 : 3);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = c;
                        bestAgree = agree;
                    }
                }
                var confirmed = best.Level == "city" && (bestAgree > 0 || best.Source is GeoSources.RipeIpmap or GeoSources.CloudflareColo);
                var status = confirmed ? "confirmed" : "single";
                var reasonText = bestAgree > 0
                    ? $"{GeoSources.Label(best.Source)}, совпадает ещё с {bestAgree} {(bestAgree == 1 ? "источником" : "источниками")}"
                    : GeoSources.Label(best.Source) + (best.Level == "city" ? "" : ", только страна");
                verdicts = verdicts.Select(v => v.Source == best.Source ? v with { Reason = "выбран: " + reasonText } : v).ToList();
                result.Add(new HopGeoDto(h.Ttl, h.Ip, best.Lat, best.Lon, status, best.Source, best.City, best.CountryCode, reasonText, verdicts, limit));
                prev = new GeoPoint(best.Lat, best.Lon, best.City ?? h.Ip, best.Source);
                continue;
            }

            var anycast = h.Info.Anycast || h.Info.Asn is { } asn && AnycastAsns.Contains(asn);
            if (anycast && h.Info.Candidates.Count > 0 || anycast && h.IsDestination)
            {
                var at = prev ?? o;
                result.Add(new HopGeoDto(h.Ttl, h.Ip, at?.Lat, at?.Lon, "anycast", null, null, null,
                    "anycast: адрес обслуживают узлы во многих городах, отвечает ближайший. Координаты из баз указывают на регистрацию, а не на ответивший узел",
                    verdicts, limit));
                continue;
            }

            if (h.Info.Candidates.Count == 0)
            {
                result.Add(new HopGeoDto(h.Ttl, h.Ip, null, null, h.Info.Pending > 0 ? "pending" : "unresolved", null, null, null,
                    "нет данных о местоположении", verdicts, limit));
                continue;
            }

            // Every source contradicts the measured RTT. Still show the most credible one on the map (the
            // check can be wrong too: asymmetric paths, a misplaced origin), but mark it as unverified.
            // It does not become the anchor for the detour check of later hops.
            var fallback = BestUnchecked(h.Info.Candidates);
            verdicts = verdicts.Select(v => v.Source == fallback.Source
                ? v with { Reason = "показан на карте без подтверждения: " + v.Reason }
                : v).ToList();
            result.Add(new HopGeoDto(h.Ttl, h.Ip, fallback.Lat, fallback.Lon, "unresolved", fallback.Source, fallback.City, fallback.CountryCode,
                $"{GeoSources.Label(fallback.Source)}: место не сходится с измеренной задержкой, показано как есть",
                verdicts, limit));
        }

        return new RouteGeoDto(sessionId, o, result, Summarize(o, result, hops), originNote);
    }

    /// <summary>Most credible candidate ignoring physics: source weight, agreement between sources, city level.</summary>
    public static GeoCandidate BestUnchecked(List<GeoCandidate> all)
    {
        GeoCandidate best = all[0];
        var bestScore = double.MinValue;
        foreach (var c in all)
        {
            var agree = all.Count(x => x.Source != c.Source && Haversine(x.Lat, x.Lon, c.Lat, c.Lon) <= AgreeKm);
            var score = GeoSources.Weight(c.Source) + agree * 1.5 - (c.Level == "city" ? 0 : 3);
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best;
    }

    /// <summary>
    /// A location trusted on its own: latency-based sources (RIPE IPmap, Cloudflare colo), or at least two
    /// independent sources agreeing at city level. A single database or router name is not enough.
    /// </summary>
    private static GeoCandidate? Established(HostInfoDto info)
    {
        var city = info.Candidates.Where(c => c.Level == "city").ToList();
        var strong = city.Where(c => c.Source is GeoSources.RipeIpmap or GeoSources.CloudflareColo).OrderByDescending(c => GeoSources.Weight(c.Source)).FirstOrDefault();
        if (strong != null) return strong;
        foreach (var c in city.OrderByDescending(c => GeoSources.Weight(c.Source)))
            if (city.Any(x => x.Source != c.Source && Haversine(x.Lat, x.Lon, c.Lat, c.Lon) <= AgreeKm)) return c;
        return null;
    }

    private static RouteSummaryDto Summarize(GeoPoint? origin, List<HopGeoDto> hops, IReadOnlyList<HopGeoInput> input)
    {
        double path = 0;
        double? lastLat = origin?.Lat, lastLon = origin?.Lon;
        foreach (var h in hops.Where(x => x.Lat != null && x.Status is "confirmed" or "single" or "anycast" or "unresolved"))
        {
            if (lastLat != null) path += Haversine(lastLat.Value, lastLon!.Value, h.Lat!.Value, h.Lon!.Value);
            lastLat = h.Lat;
            lastLon = h.Lon;
        }
        var unresolved = hops.Count(x => x.Status == "unresolved");
        var destIn = input.FirstOrDefault(x => x.IsDestination);
        var dest = destIn == null ? null : hops.FirstOrDefault(x => x.Ttl == destIn.Ttl);
        if (origin == null || dest == null || dest.Lat == null || dest.Status is not ("confirmed" or "single"))
            return new RouteSummaryDto(path, null, null, destIn?.Best, null, unresolved);
        var direct = Haversine(origin.Lat, origin.Lon, dest.Lat!.Value, dest.Lon!.Value);
        var min = direct / KmPerMsRtt;
        double? stretch = destIn!.Best is { } m && min > 0.5 ? m / min : null;
        return new RouteSummaryDto(path, direct, min, destIn.Best, stretch, unresolved);
    }

    public static double Haversine(double lat1, double lon1, double lat2, double lon2) => HostnameHints.DistanceKm(lat1, lon1, lat2, lon2);
}
