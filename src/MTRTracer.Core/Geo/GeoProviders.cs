namespace MTRTracer.Core.Geo;

/// <summary>
/// Which data sources may be used. The online ones additionally require the "online refinement" master
/// switch (<see cref="Protocol.TraceOptions.GeoOnline"/>). Stored in settings.json.
/// </summary>
public sealed class GeoProviders
{
    /// <summary>Offline MaxMind-format database (DB-IP Lite or an imported file).</summary>
    public bool OfflineDb { get; set; } = true;
    /// <summary>City codes in router names (reverse DNS); works offline.</summary>
    public bool HostnameHints { get; set; } = true;
    /// <summary>RIPE IPmap: latency-based geolocation from RIPE Atlas.</summary>
    public bool RipeIpmap { get; set; } = true;
    /// <summary>RIPEstat: MaxMind GeoLite location and live BGP prefix/AS.</summary>
    public bool RipeStat { get; set; } = true;
    /// <summary>ip-api.com: ISP and organisation names, fallback location.</summary>
    public bool IpApi { get; set; } = true;
    /// <summary>Cloudflare speed.cloudflare.com/meta: your own public IP and location.</summary>
    public bool CloudflareSelf { get; set; } = true;
    /// <summary>Cloudflare /cdn-cgi/trace: which data centre answers for a site behind Cloudflare.</summary>
    public bool CloudflareColo { get; set; } = true;
    /// <summary>Team Cymru DNS: AS number when no other source has it.</summary>
    public bool TeamCymru { get; set; } = true;

    public GeoProviders Clone() => (GeoProviders)MemberwiseClone();
}
