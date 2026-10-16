using MTRTracer.Core;
using MTRTracer.Core.Geo;
using MTRTracer.Core.Mtr;
using MTRTracer.Core.Protocol;
using MTRTracer.Core.Updates;

// Some tests switch the global interface language; run them one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MTRTracer.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.1.2", "1.1.1", true)]
    [InlineData("v1.2", "1.1.9", true)]
    [InlineData("1.1.1", "1.1.1", false)]
    [InlineData("v1.0.9", "1.1.0", false)]
    [InlineData("release-2.0.0", "1.9.9", true)]
    [InlineData(null, "1.1.1", false)]
    [InlineData("nightly", "1.1.1", false)]
    public void ComparesVersions(string? tag, string current, bool newer) => Assert.Equal(newer, UpdateChecker.IsNewer(tag, current));

    [Theory]
    [InlineData("someone/MTRTracer", "someone/MTRTracer")]
    [InlineData("https://github.com/someone/MTRTracer", "someone/MTRTracer")]
    [InlineData("https://github.com/someone/MTRTracer.git", "someone/MTRTracer")]
    [InlineData("https://github.com/someone/MTRTracer/releases", "someone/MTRTracer")]
    [InlineData("", UpdateChecker.DefaultRepo)]
    [InlineData("not a repo", UpdateChecker.DefaultRepo)]
    [InlineData("../../etc", UpdateChecker.DefaultRepo)]
    public void NormalizesRepo(string input, string expected) => Assert.Equal(expected, UpdateChecker.NormalizeRepo(input));
}

public class LocTests
{
    [Fact]
    public void AnalyzerSpeaksEnglish()
    {
        static HopDto Hop(int ttl, double loss, double avg, bool dest = false) =>
            new(ttl, $"10.0.0.{ttl}", [], 50, (int)Math.Round(50 * (100 - loss) / 100), loss, avg, avg, avg, avg, 0.5, avg, 1, 1, 2, 1, "ok", [], dest, 0);
        var hops = new List<HopDto> { Hop(1, 0, 1), Hop(2, 40, 10), Hop(3, 0, 12), Hop(4, 0, 13, dest: true) };
        try
        {
            Loc.Language = "en";
            var notes = new Analyzer().Analyze(hops, 4, null);
            Assert.Contains(notes, n => n.Code == "healthy" && n.Text.StartsWith("Route is stable"));
            Assert.Contains(notes, n => n.Code == "rate-limit" && n.Text.Contains("rate-limits ICMP"));
        }
        finally
        {
            Loc.Language = "ru";
        }
        Assert.Contains(new Analyzer().Analyze(hops, 4, null), n => n.Code == "healthy" && n.Text.StartsWith("Маршрут стабилен"));
    }

    [Fact]
    public void CityNamesFollowLanguage()
    {
        try
        {
            Loc.Language = "en";
            Assert.Equal("Amsterdam", HostnameHints.Match("be3061.ccr41.ams03.atlas.cogentco.com")!.Value.Geo.City);
            Assert.Equal("Frankfurt", HostnameHints.ByCode("FRA")!.City);
            Assert.Contains(HostnameHints.KnownCities(), c => c.City == "Warsaw");
        }
        finally
        {
            Loc.Language = "ru";
        }
        Assert.Equal("Амстердам", HostnameHints.Match("be3061.ccr41.ams03.atlas.cogentco.com")!.Value.Geo.City);
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("ru", "ru")]
    public void NormalizesLanguage(string input, string expected) => Assert.Equal(expected, Loc.Normalize(input));
}

public class GeoDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtrtracer-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EmptyDirectoryHasNoReleases()
    {
        using var db = new DbIpDatabase(_dir);
        Assert.Empty(db.Releases());
        Assert.False(db.Ready);
        Assert.Equal("missing", db.Status);
    }

    [Fact]
    public void RejectsFilesThatAreNotMaxMindDatabases()
    {
        using var db = new DbIpDatabase(_dir);
        var junk = Path.Combine(_dir, "junk.mmdb");
        File.WriteAllText(junk, "definitely not a database");
        var e = Assert.Throws<CommandException>(() => db.Import(junk));
        Assert.Equal("bad-file", e.Code);
        Assert.False(File.Exists(Path.Combine(_dir, "custom-city.mmdb")));
    }

    [Theory]
    [InlineData(null, GeoDbSetting.DefaultUrlTemplate)]
    [InlineData("http://example.com/{name}-{month}.mmdb.gz", GeoDbSetting.DefaultUrlTemplate)]
    [InlineData("https://mirror.example/{name}.mmdb.gz", GeoDbSetting.DefaultUrlTemplate)]
    [InlineData("https://mirror.example/{name}-{month}.mmdb.gz", "https://mirror.example/{name}-{month}.mmdb.gz")]
    public void NormalizesUrlTemplate(string? input, string expected) =>
        Assert.Equal(expected, new GeoDbSetting { UrlTemplate = input! }.Normalize().UrlTemplate);

    [Fact]
    public void PinnedReleaseIsNotAutoUpdated()
    {
        using var db = new DbIpDatabase(_dir, new GeoDbSetting { Active = "2026-01" });
        // No database at all: something must be downloaded regardless of the pin.
        Assert.True(db.NeedsUpdate());
    }

    [Fact]
    public void DisabledDatabaseAnswersNothing()
    {
        using var db = new DbIpDatabase(_dir) { Enabled = false };
        Assert.Null(db.LookupCity(System.Net.IPAddress.Parse("198.51.100.1")));
        Assert.Null(db.LookupAsn(System.Net.IPAddress.Parse("198.51.100.1")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}

public class ExternalLinkTests
{
    [Theory]
    [InlineData("https://github.com/someone/MTRTracer/releases", true)]
    [InlineData("https://db-ip.com/db/lite.php", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://evil.example/github.com", false)]
    [InlineData("file:///c:/windows", false)]
    [InlineData(null, false)]
    public void AllowsOnlyKnownHttpsHosts(string? url, bool ok) => Assert.Equal(ok, ExternalLinks.IsAllowed(url));
}
