using System.Text.Json.Nodes;
using MTRTracer.Core.Geo;
using MTRTracer.Core.History;
using MTRTracer.Core.Protocol;

namespace MTRTracer.Tests;

public class RouteGeoSolverTests
{
    private static readonly GeoPoint Warsaw = new(52.2297, 21.0122, "Варшава", "manual");

    private static GeoCandidate C(string source, double lat, double lon, string city, string level = "city") =>
        new(source, lat, lon, city, null, null, level, 0);

    private static HostInfoDto Info(string ip, int? asn, params GeoCandidate[] c) =>
        new() { Ip = ip, Kind = "public", Asn = asn, Candidates = [.. c] };

    private static HopGeoInput Hop(int ttl, double best, HostInfoDto info, bool dest = false) =>
        new(ttl, info.Ip, best, 20, info, dest);

    [Fact]
    public void RejectsZigzagThatBreaksSpeedOfLight()
    {
        // Warsaw → Stockholm (32 ms) → a hop DB-IP puts in New York at 34 ms → Frankfurt (45 ms).
        var sto = Info("62.115.137.38", 1299, C(GeoSources.Hint, 59.33, 18.07, "Стокгольм"), C(GeoSources.DbIp, 59.33, 18.07, "Stockholm"));
        var bad = Info("62.115.124.118", 1299, C(GeoSources.DbIp, 40.71, -74.0, "New York"), C(GeoSources.MaxMind, 53.55, 9.99, "Hamburg"));
        var fra = Info("142.250.164.34", 15169, C(GeoSources.RipeIpmap, 50.11, 8.68, "Frankfurt"));
        var r = RouteGeoSolver.Solve("s", Warsaw, [Hop(1, 32, sto), Hop(2, 34, bad), Hop(3, 45, fra, true)]);

        Assert.Equal("confirmed", r.Hops[0].Status);
        // New York is ~7 500 km away: 34 ms cannot cover that. The Hamburg candidate fits and is chosen.
        Assert.Equal(GeoSources.MaxMind, r.Hops[1].Source);
        Assert.Contains(r.Hops[1].Verdicts, v => v.Source == GeoSources.DbIp && !v.Accepted);
        Assert.Equal("confirmed", r.Hops[2].Status);
    }

    [Fact]
    public void AnycastDestinationIsNotPlacedAtRegistryAddress()
    {
        // 1.1.1.1 answers in 4 ms from Warsaw, while the databases say San Francisco / Australia.
        var cf = Info("1.1.1.1", 13335, C(GeoSources.DbIp, 37.77, -122.42, "San Francisco"), C(GeoSources.IpApi, -27.47, 153.02, "Brisbane"));
        var isp = Info("198.51.100.10", 64500, C(GeoSources.Hint, 52.23, 21.01, "Варшава"));
        var r = RouteGeoSolver.Solve("s", Warsaw, [Hop(1, 3, isp), Hop(2, 4, cf, true)]);

        Assert.Equal("anycast", r.Hops[1].Status);
        Assert.NotNull(r.Hops[1].Lat);
        Assert.True(RouteGeoSolver.Haversine(52.23, 21.01, r.Hops[1].Lat!.Value, r.Hops[1].Lon!.Value) < 50);
        // No contradictory "physical minimum" for an anycast destination.
        Assert.Null(r.Summary.MinRttMs);
    }

    [Fact]
    public void UnresolvedWhenEverySourceIsImpossible()
    {
        var odd = Info("203.0.113.10", 64500, C(GeoSources.DbIp, 40.71, -74.0, "New York"));
        var r = RouteGeoSolver.Solve("s", Warsaw, [Hop(1, 12, odd)]);
        Assert.Equal("unresolved", r.Hops[0].Status);
        // Still drawn where the best source says, just marked as not matching the RTT.
        Assert.Equal(40.71, r.Hops[0].Lat);
        Assert.Equal(GeoSources.DbIp, r.Hops[0].Source);
        Assert.Equal(1, r.Summary.Unresolved);
    }

    [Fact]
    public void LaterHopRttBoundsEarlierHop()
    {
        // Hop 1 replies slowly (ICMP slow path, 80 ms) but hop 2 behind it answers in 10 ms:
        // hop 1 must be within ~10 ms, so New York is impossible even though 80 ms would allow it.
        var slow = Info("198.51.100.1", 64501, C(GeoSources.DbIp, 40.71, -74.0, "New York"), C(GeoSources.MaxMind, 52.23, 21.01, "Warsaw"));
        var next = Info("198.51.100.2", 64501, C(GeoSources.Hint, 52.23, 21.01, "Варшава"));
        var r = RouteGeoSolver.Solve("s", Warsaw, [Hop(1, 80, slow), Hop(2, 10, next)]);
        Assert.Equal(GeoSources.MaxMind, r.Hops[0].Source);
    }

    [Fact]
    public void AgreementMakesConfirmed()
    {
        var a = Info("192.0.2.1", 1, C(GeoSources.DbIp, 52.37, 4.90, "Amsterdam"), C(GeoSources.MaxMind, 52.38, 4.89, "Amsterdam"));
        var r = RouteGeoSolver.Solve("s", Warsaw, [Hop(1, 45, a, true)]);
        Assert.Equal("confirmed", r.Hops[0].Status);
        Assert.NotNull(r.Summary.MinRttMs);
        Assert.True(r.Summary.MeasuredMs >= r.Summary.MinRttMs);
    }

    [Fact]
    public void LocalHopsSitAtOrigin()
    {
        var lan = new HostInfoDto { Ip = "10.0.0.1", Kind = "private" };
        var r = RouteGeoSolver.Solve("s", Warsaw, [new HopGeoInput(1, "10.0.0.1", 1, 10, lan, false)]);
        Assert.Equal("local", r.Hops[0].Status);
        Assert.Equal(Warsaw.Lat, r.Hops[0].Lat);
    }
}

public class SourceParserTests
{
    [Fact]
    public void CloudflareMetaWithStringCoordinatesAndColoObject()
    {
        const string json = """
            {"hostname":"speed.cloudflare.com","clientIp":"165.227.106.14","httpProtocol":"HTTP/1.1","asn":14061,
             "asOrganization":"DigitalOcean, LLC","country":"US","city":"Clifton","region":"New Jersey","postalCode":"07015",
             "latitude":"40.85843","longitude":"-74.16376",
             "colo":{"iata":"EWR","lat":40.692501,"lon":-74.168701,"cca2":"US","region":"North America","city":"Newark"}}
            """;
        var m = CloudflareMetaClient.Parse(json)!;
        Assert.Equal("165.227.106.14", m.ClientIp);
        Assert.Equal(14061, m.Asn);
        Assert.Equal(40.85843, m.Latitude!.Value, 5);
        Assert.Equal(-74.16376, m.Longitude!.Value, 5);
        Assert.Equal("EWR", m.ColoIata);
        Assert.Equal(40.692501, m.ColoLat!.Value, 5);
    }

    [Fact]
    public void CloudflareMetaWithLegacyStringColo()
    {
        var m = CloudflareMetaClient.Parse("""{"clientIp":"1.2.3.4","colo":"FRA","latitude":"50.1","longitude":"8.6"}""")!;
        Assert.Equal("FRA", m.ColoIata);
        Assert.Null(m.ColoLat);
    }

    [Theory]
    [InlineData("""{"location":{"cityNameAscii":"Amsterdam","countryCodeAlpha2":"NL","latitude":52.37403,"longitude":4.88969,"type":"city","score":12.3}}""")]
    [InlineData("""{"locations":[{"cityName":"Amsterdam","countryCodeAlpha2":"NL","latitude":52.37403,"longitude":4.88969,"type":"city"}]}""")]
    [InlineData("""[{"cityNameAscii":"Amsterdam","countryCodeAlpha2":"NL","latitude":"52.37403","longitude":"4.88969"}]""")]
    public void RipeIpmapShapes(string json)
    {
        var (geo, anycast) = RipeIpmapClient.Parse(JsonNode.Parse(json), 1);
        Assert.NotNull(geo);
        Assert.Equal("Amsterdam", geo!.City);
        Assert.Equal("NL", geo.CountryCode);
        Assert.Equal(52.37403, geo.Lat, 5);
        Assert.False(anycast);
    }

    [Fact]
    public void RipeIpmapAnycastFlag()
    {
        var (_, anycast) = RipeIpmapClient.Parse(JsonNode.Parse("""{"location":null,"metadata":{"latency":{"anycast":true}}}"""), 1);
        Assert.True(anycast);
    }

    [Fact]
    public void RipeStatMaxMindPicksCityLevel()
    {
        var data = JsonNode.Parse("""
            {"located_resources":[{"resource":"193.0.0.0/21","locations":[
              {"country":"NL","city":"","resources":["193.0.0.0/22"],"latitude":52.38,"longitude":4.9,"covered_percentage":60},
              {"country":"NL","city":"Amsterdam","resources":["193.0.4.0/22"],"latitude":52.37,"longitude":4.89,"covered_percentage":40}]}]}
            """);
        var g = RipeStatClient.ParseMaxMind(data, 1)!;
        Assert.Equal("Amsterdam", g.City);
        Assert.Equal("city", g.Level);
    }

    [Fact]
    public void ColoCodes()
    {
        Assert.Equal("Франкфурт", HostnameHints.ByCode("FRA")!.City);
        Assert.Equal("Стокгольм", HostnameHints.ByCode("arn")!.City);
        Assert.Null(HostnameHints.ByCode("ZZZ"));
    }
}

public class HistoryStoreTests
{
    [Fact]
    public void RoundTripAndCompare()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mtr-history-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new HistoryStore(path))
            {
                var opts = new TraceOptions();
                HopDto H(int ttl, string ip, double avg, double loss, bool dest = false) =>
                    new(ttl, ip, [], 60, 60, loss, avg, avg, avg, avg, 0.1, avg, 0.1, 0.1, 0.1, 0.1, "ok", [], dest, 0);

                var r1 = store.StartRun(new RunStart("google.com", "google.com", "google.com", "142.250.185.78", "normal", "a", "Ethernet", "10.0.0.2", null, 1000, opts));
                store.AddSnapshot(r1, new HopsSnapshotDto("s1", 60, 2, [H(1, "10.0.0.1", 1, 0), H(2, "142.250.185.78", 45, 0, true)], null, [], 60000), _ => null);
                store.EndRun(r1, 2000);

                var r2 = store.StartRun(new RunStart("google.com", "google.com", "google.com", "142.250.185.78", "normal", "a", "Ethernet", "10.0.0.2", null, 3000, opts));
                store.AddSnapshot(r2, new HopsSnapshotDto("s2", 60, 2, [H(1, "10.0.0.1", 1, 0), H(2, "142.250.185.78", 61, 2, true)], null, [], 60000), _ => null);
                store.EndRun(r2, 4000);

                var empty = store.StartRun(new RunStart("x.org", "x.org", null, null, "normal", null, null, null, null, 5000, opts));
                store.EndRun(empty, 5001); // never got a snapshot → dropped

                var targets = store.Targets();
                Assert.Single(targets);
                Assert.Equal(2, targets[0].Runs);
                Assert.Equal(61, targets[0].LastAvg);

                var runs = store.Runs("google.com");
                Assert.Equal(r2, runs[0].Id);
                Assert.Equal(runs[0].RouteHash, runs[1].RouteHash);
                Assert.Equal(r1, store.LastRun("google.com", r2)!.Id);

                var detail = store.Run(r1)!;
                Assert.Equal(2, detail.Hops.Count);
                Assert.Single(detail.Snapshots);

                store.DeleteTarget("google.com");
                Assert.Empty(store.Targets());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch { }
        }
    }
}

public class OriginSanityTests
{
    private static HostInfoDto ProviderRouter(string ip) => new()
    {
        Ip = ip, Kind = "public", Asn = 64500,
        Candidates =
        [
            new GeoCandidate(GeoSources.Hint, 52.23, 21.01, "Варшава", null, "PL", "city", 0),
            new GeoCandidate(GeoSources.MaxMind, 52.24, 21.02, "Warsaw", null, "PL", "city", 0),
        ],
    };

    [Fact]
    public void VpnExitAsOriginIsReplacedByProviderRouter()
    {
        // Origin came back as the VPN exit in Stockholm, but the provider's router answers in 4 ms from Warsaw.
        var stockholm = new GeoPoint(59.33, 18.07, "Stockholm", "cloudflare");
        var lan = new HostInfoDto { Ip = "10.0.0.1", Kind = "private" };
        var r = RouteGeoSolver.Solve("s", stockholm,
        [
            new HopGeoInput(1, "10.0.0.1", 1, 20, lan, false),
            new HopGeoInput(2, "198.51.100.10", 4, 20, ProviderRouter("198.51.100.10"), false),
        ]);
        Assert.Equal("first-hop", r.Origin!.Source);
        Assert.True(RouteGeoSolver.Haversine(52.23, 21.01, r.Origin.Lat, r.Origin.Lon) < 30);
        Assert.NotNull(r.OriginNote);
        Assert.Equal("confirmed", r.Hops[1].Status);
    }

    [Fact]
    public void CorrectOriginIsKept()
    {
        var home = new GeoPoint(52.23, 21.02, "Варшава", "cloudflare");
        var r = RouteGeoSolver.Solve("s", home, [new HopGeoInput(1, "198.51.100.10", 4, 20, ProviderRouter("198.51.100.10"), false)]);
        Assert.Equal("cloudflare", r.Origin!.Source);
        Assert.Null(r.OriginNote);
    }

    [Fact]
    public void SingleWeakSourceDoesNotOverrideOrigin()
    {
        // Only DB-IP says the router is far away: not enough evidence to move the user.
        var home = new GeoPoint(52.23, 21.02, "Варшава", "cloudflare");
        var weak = new HostInfoDto { Ip = "203.0.113.1", Kind = "public", Candidates = [new GeoCandidate(GeoSources.DbIp, 40.7, -74.0, "New York", null, "US", "city", 0)] };
        var r = RouteGeoSolver.Solve("s", home, [new HopGeoInput(1, "203.0.113.1", 4, 20, weak, false)]);
        Assert.Equal("cloudflare", r.Origin!.Source);
        Assert.Equal("unresolved", r.Hops[0].Status);
    }

    [Fact]
    public void ManualOriginOnlyWarns()
    {
        var berlin = new GeoPoint(52.52, 13.40, "Берлин", "manual");
        var r = RouteGeoSolver.Solve("s", berlin, [new HopGeoInput(1, "198.51.100.10", 2, 20, ProviderRouter("198.51.100.10"), false)]);
        Assert.Equal("manual", r.Origin!.Source);
        Assert.NotNull(r.OriginNote);
    }
}
