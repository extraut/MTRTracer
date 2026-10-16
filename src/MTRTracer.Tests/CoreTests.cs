using System.Net;
using MTRTracer.Core.Dns;
using MTRTracer.Core.Geo;
using MTRTracer.Core.Mtr;
using MTRTracer.Core.Protocol;

namespace MTRTracer.Tests;

internal static class TestSetup
{
    /// <summary>Expected texts in the tests are Russian; CI runners have an English culture.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => MTRTracer.Core.Loc.Language = "ru";
}

public class LatencyStatsTests
{
    [Fact]
    public void ComputesMtrStatistics()
    {
        var s = new LatencyStats();
        double[] rtts = [10, 12, 11, 15, 9];
        foreach (var r in rtts)
        {
            s.AddSent();
            s.AddReply(r);
        }
        s.AddSent();
        s.AddLoss();

        Assert.Equal(6, s.Sent);
        Assert.Equal(5, s.Recv);
        Assert.Equal(100.0 / 6, s.LossPct, 6);
        Assert.Equal(9, s.Best);
        Assert.Equal(15, s.Worst);
        Assert.Equal(9, s.Last);
        Assert.Equal(11.4, s.Avg, 9);
        // Sample standard deviation of {10,12,11,15,9}
        Assert.Equal(Math.Sqrt(5.3), s.StDev, 9);
        Assert.Equal(Math.Pow(10.0 * 12 * 11 * 15 * 9, 1 / 5.0), s.GMean, 9);
        // Jitter: |12-10|=2, |11-12|=1, |15-11|=4, |9-15|=6
        Assert.Equal(6, s.Jitter);
        Assert.Equal(13 / 4.0, s.JAvg, 9);
        Assert.Equal(6, s.JMax);
        // RFC 3550: J += (|D| - J) / 16 starting from the first difference
        var j = 2.0;
        foreach (var d in new[] { 1.0, 4, 6 }) j += (d - j) / 16;
        Assert.Equal(j, s.JInt, 9);
        Assert.Equal(1, s.ConsecutiveLoss);
        var h = s.History();
        Assert.Equal(6, h.Length);
        Assert.True(double.IsNaN(h[^1]));
        Assert.Equal(10, h[0]);
    }

    [Fact]
    public void HistoryIsBounded()
    {
        var s = new LatencyStats();
        for (var i = 0; i < LatencyStats.HistorySize + 50; i++)
        {
            s.AddSent();
            s.AddReply(i);
        }
        var h = s.History();
        Assert.Equal(LatencyStats.HistorySize, h.Length);
        Assert.Equal(50, h[0]);
        Assert.Equal(LatencyStats.HistorySize + 49, h[^1]);
    }
}

public class TargetParserTests
{
    [Theory]
    [InlineData("1.1.1.1", "1.1.1.1", null)]
    [InlineData("  google.com  ", "google.com", null)]
    [InlineData("https://www.Example.com/path?q=1#x", "www.example.com", 443)]
    [InlineData("http://example.com:8080/", "example.com", 8080)]
    [InlineData("example.com:22", "example.com", 22)]
    [InlineData("[2606:4700:4700::1111]:443", "2606:4700:4700::1111", 443)]
    [InlineData("2606:4700:4700::1111", "2606:4700:4700::1111", null)]
    [InlineData("user@host.example.org", "host.example.org", null)]
    [InlineData("яндекс.рф", "xn--d1acpjx3f.xn--p1ai", null)]
    [InlineData("example.com.", "example.com", null)]
    public void Parses(string input, string host, int? port)
    {
        var t = TargetParser.Parse(input);
        Assert.NotNull(t);
        Assert.Equal(host, t!.Host);
        Assert.Equal(port, t.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("foo bar")]
    [InlineData("a,b")]
    public void Rejects(string input) => Assert.Null(TargetParser.Parse(input));
}

public class DnsTests
{
    [Fact]
    public void BuildsQuery()
    {
        var q = MiniDnsClient.BuildQuery(0x1234, "example.com", DnsType.A);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }, q[..12]);
        Assert.Equal(new byte[] { 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1 }, q[12..]);
    }

    [Fact]
    public void ParsesCompressedAnswers()
    {
        // Response for example.com: CNAME www -> example.com (compressed), A 93.184.216.34, TXT "a|b"
        var msg = new List<byte> { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, 3, 0, 0, 0, 0 };
        msg.AddRange([7, .. "example"u8.ToArray(), 3, .. "com"u8.ToArray(), 0, 0, 1, 0, 1]);
        // Answer 1: name ptr to 12, CNAME, rdata = "www" + ptr 12
        msg.AddRange([0xC0, 12, 0, 5, 0, 1, 0, 0, 0, 60, 0, 6, 3, (byte)'w', (byte)'w', (byte)'w', 0xC0, 12]);
        // Answer 2: A
        msg.AddRange([0xC0, 12, 0, 1, 0, 1, 0, 0, 1, 0, 0, 4, 93, 184, 216, 34]);
        // Answer 3: TXT with two strings
        msg.AddRange([0xC0, 12, 0, 16, 0, 1, 0, 0, 0, 1, 0, 6, 2, (byte)'a', (byte)'|', 2, (byte)'b', (byte)'c']);

        var (rcode, answers) = MiniDnsClient.Parse(msg.ToArray());
        Assert.Equal(0, rcode);
        Assert.Equal(3, answers.Count);
        Assert.Equal("www.example.com", answers[0].Data);
        Assert.Equal("example.com", answers[0].Name);
        Assert.Equal("93.184.216.34", answers[1].Data);
        Assert.Equal("a|bc", answers[2].Data);
    }

    [Fact]
    public void ReverseNames()
    {
        Assert.Equal("4.3.2.1.in-addr.arpa", MiniDnsClient.ReverseName(IPAddress.Parse("1.2.3.4")));
        Assert.EndsWith(".1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.ip6.arpa".TrimStart('.'),
            MiniDnsClient.ReverseName(IPAddress.Parse("::1")));
    }
}

public class GeoTests
{
    [Theory]
    [InlineData("be3061.ccr41.ams03.atlas.cogentco.com", "Амстердам")]
    [InlineData("ae-1.r20.frnkge13.de.bb.gin.ntt.net", "Франкфурт")]
    [InlineData("ae-2-3202.edge4.Frankfurt1.Level3.net", "Франкфурт")]
    [InlineData("xe-0-0-0.lon-core1.example.net", "Лондон")]
    [InlineData("waw-b1-ae7.ix.example.net", "Варшава")]
    public void MatchesRouterNames(string host, string city)
    {
        var m = HostnameHints.Match(host);
        Assert.NotNull(m);
        Assert.Equal(city, m!.Value.Geo.City);
    }

    [Theory]
    [InlineData("one.one.one.one")]
    [InlineData("customer.example.com")]
    [InlineData(null)]
    public void NoFalseHints(string? host) => Assert.Null(HostnameHints.Match(host));

    [Fact]
    public void SpeedOfLight()
    {
        // Warsaw -> Frankfurt is ~890 km: needs at least ~9 ms RTT.
        var d = HostnameHints.DistanceKm(52.2297, 21.0122, 50.1109, 8.6821);
        Assert.InRange(d, 850, 950);
        Assert.False(HostnameHints.IsPhysicallyPossible(d, 5));
        Assert.True(HostnameHints.IsPhysicallyPossible(d, 30));
    }

    [Theory]
    [InlineData("10.0.0.1", "private")]
    [InlineData("172.20.1.1", "private")]
    [InlineData("192.168.1.1", "private")]
    [InlineData("100.64.0.1", "cgnat")]
    [InlineData("169.254.1.1", "link-local")]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("8.8.8.8", "public")]
    [InlineData("fe80::1", "link-local")]
    [InlineData("fd00::1", "ula")]
    [InlineData("2606:4700:4700::1111", "public")]
    public void Classifies(string ip, string kind) => Assert.Equal(kind, IpClassifier.Classify(IPAddress.Parse(ip)));
}

public class AnalyzerTests
{
    private static HopDto Hop(int ttl, double loss, double? avg, int sent = 50, bool dest = false, double? javg = 1) =>
        new(ttl, $"10.0.0.{ttl}", [], sent, (int)Math.Round(sent * (100 - loss) / 100), loss, avg, avg, avg, avg, 0.5, avg,
            1, javg, 2, 1, "ok", [], dest, 0);

    [Fact]
    public void IntermediateLossIsRateLimiting()
    {
        var hops = new List<HopDto> { Hop(1, 0, 1), Hop(2, 40, 10), Hop(3, 0, 12), Hop(4, 0, 13, dest: true) };
        var notes = new Analyzer().Analyze(hops, 4, null);
        Assert.Contains(notes, n => n.Code == "rate-limit" && n.Ttl == 2);
        Assert.Contains(notes, n => n.Code == "healthy");
        Assert.DoesNotContain(notes, n => n.Level == "error");
    }

    [Fact]
    public void LossThatPersistsPointsAtOrigin()
    {
        var hops = new List<HopDto> { Hop(1, 0, 1), Hop(2, 0, 5), Hop(3, 20, 12), Hop(4, 18, 13), Hop(5, 21, 14, dest: true) };
        var notes = new Analyzer().Analyze(hops, 5, null);
        Assert.Contains(notes, n => n.Code == "loss-origin" && n.Ttl == 3);
    }

    [Fact]
    public void LatencyJump()
    {
        var hops = new List<HopDto> { Hop(1, 0, 1), Hop(2, 0, 5), Hop(3, 0, 60), Hop(4, 0, 62, dest: true) };
        var notes = new Analyzer().Analyze(hops, 4, null);
        Assert.Contains(notes, n => n.Code == "latency-jump" && n.Ttl == 3);
    }
}
