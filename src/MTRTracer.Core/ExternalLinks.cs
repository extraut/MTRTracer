namespace MTRTracer.Core;

/// <summary>Links the page may ask the host to open in the system browser.</summary>
public static class ExternalLinks
{
    private static readonly string[] Hosts =
    [
        "github.com", "extraut.github.io", "db-ip.com", "www.db-ip.com", "www.maxmind.com", "dev.maxmind.com",
        "ipinfo.io", "ipmap.ripe.net", "stat.ripe.net", "ip-api.com", "speed.cloudflare.com", "www.naturalearthdata.com",
    ];

    public static bool IsAllowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        Hosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);
}
