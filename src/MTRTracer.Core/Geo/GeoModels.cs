using System.Net;
using System.Net.Sockets;

namespace MTRTracer.Core.Geo;

public sealed record GeoDto(
    double Lat,
    double Lon,
    string? City,
    string? Region,
    string? Country,
    string? CountryCode,
    string Source,
    string Accuracy);

/// <summary>One opinion about where an address is, from one source, with the time it was obtained.</summary>
public sealed record GeoCandidate(
    string Source,
    double Lat,
    double Lon,
    string? City,
    string? Region,
    string? CountryCode,
    string Level,
    long FetchedAt,
    string? Note = null)
{
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public GeoDto ToGeo() => new(Lat, Lon, City, Region, null, CountryCode, Source, Level);
}

public static class GeoSources
{
    public const string RipeIpmap = "ripe-ipmap";
    public const string CloudflareColo = "cf-colo";
    public const string Hint = "hint";
    public const string MaxMind = "maxmind";
    public const string DbIp = "dbip";
    public const string IpApi = "ipapi";

    /// <summary>
    /// Trust order for router geolocation. Latency-based (RIPE IPmap) and operator-observed (Cloudflare colo)
    /// beat router naming conventions, which beat registry-derived commercial databases.
    /// </summary>
    public static double Weight(string source) => source switch
    {
        RipeIpmap => 6,
        CloudflareColo => 6,
        Hint => 5,
        MaxMind => 3,
        DbIp => 2,
        IpApi => 1.5,
        _ => 1,
    };

    public static string Label(string source) => source switch
    {
        RipeIpmap => "RIPE IPmap",
        CloudflareColo => "Cloudflare colo",
        Hint => "имя роутера",
        MaxMind => "MaxMind GeoLite (RIPEstat)",
        DbIp => "DB-IP Lite",
        IpApi => "ip-api.com",
        _ => source,
    };
}

public sealed class HostInfoDto
{
    public required string Ip { get; init; }
    public required string Kind { get; init; }
    public string? Hostname { get; set; }
    public bool HostnameDone { get; set; }
    public int? Asn { get; set; }
    public string? AsName { get; set; }
    public string? AsSource { get; set; }
    public string? Prefix { get; set; }
    public string? Registry { get; set; }
    public string? Isp { get; set; }
    public string? Org { get; set; }
    public bool Anycast { get; set; }
    public string? HintToken { get; set; }
    public List<GeoCandidate> Candidates { get; set; } = [];
    /// <summary>Online lookups still running for this address.</summary>
    public int Pending { get; set; }

    public HostInfoDto Clone()
    {
        var c = (HostInfoDto)MemberwiseClone();
        c.Candidates = [.. Candidates];
        return c;
    }

    /// <summary>Best location without RTT context (connections table, exports): highest source weight, city level first.</summary>
    public GeoCandidate? Best() => Candidates
        .OrderByDescending(c => c.Level == "city")
        .ThenByDescending(c => GeoSources.Weight(c.Source))
        .FirstOrDefault();

    public void AddCandidate(GeoCandidate? c)
    {
        if (c == null || double.IsNaN(c.Lat) || double.IsNaN(c.Lon)) return;
        if (c.Lat == 0 && c.Lon == 0) return;
        Candidates.RemoveAll(x => x.Source == c.Source);
        Candidates.Add(c);
    }
}

public static class IpClassifier
{
    /// <summary>public | private | cgnat | link-local | loopback | multicast | ula | reserved</summary>
    public static string Classify(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return "loopback";
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168) return "private";
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return "cgnat";
            if (b[0] == 169 && b[1] == 254) return "link-local";
            if (b[0] >= 224 && b[0] <= 239) return "multicast";
            if (b[0] == 0 || b[0] >= 240 || b[0] == 198 && b[1] is 18 or 19 || b[0] == 192 && b[1] == 0 && b[2] == 2) return "reserved";
            return "public";
        }
        if (ip.IsIPv6LinkLocal) return "link-local";
        if (ip.IsIPv6Multicast) return "multicast";
        var first = ip.GetAddressBytes()[0];
        if ((first & 0xFE) == 0xFC) return "ula";
        if (ip.IsIPv6SiteLocal) return "private";
        return "public";
    }

    public static bool IsPublic(IPAddress ip) => Classify(ip) == "public";
}
