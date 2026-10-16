namespace MTRTracer.Core.Protocol;

/// <summary>Receives events destined for the UI. The host coalesces and forwards them to WebView2.</summary>
public interface IEventSink
{
    void Emit(string type, object payload);
}

public sealed class TraceOptions
{
    public int IntervalMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 2000;
    public int PacketSize { get; set; } = 64;
    public int MaxHops { get; set; } = 30;
    public int Burst { get; set; } = 1;
    public bool DontFragment { get; set; }
    public string IpVersion { get; set; } = "auto";
    public int MaxRounds { get; set; }
    public bool ResolveNames { get; set; } = true;
    public bool TcpFallback { get; set; } = true;
    public int TcpPort { get; set; } = 443;
    public bool GeoOnline { get; set; } = true;
    public string Payload { get; set; } = "MTRTracer";

    public TraceOptions Normalize()
    {
        IntervalMs = Math.Clamp(IntervalMs, 100, 60_000);
        TimeoutMs = Math.Clamp(TimeoutMs, 200, 10_000);
        PacketSize = Math.Clamp(PacketSize, 0, 65_000);
        MaxHops = Math.Clamp(MaxHops, 1, 64);
        Burst = Math.Clamp(Burst, 1, 20);
        MaxRounds = Math.Clamp(MaxRounds, 0, 1_000_000);
        TcpPort = Math.Clamp(TcpPort, 1, 65535);
        if (IpVersion is not ("auto" or "4" or "6")) IpVersion = "auto";
        if (Payload.Length > 64) Payload = Payload[..64];
        return this;
    }

    public TraceOptions Clone() => (TraceOptions)MemberwiseClone();
}

public sealed record ResponderDto(string Ip, int Count, double Share, double? Avg, double? Best, double? Last);

public sealed record HopDto(
    int Ttl,
    string? Primary,
    List<ResponderDto> Responders,
    int Sent,
    int Recv,
    double Loss,
    double? Last,
    double? Best,
    double? Avg,
    double? Worst,
    double? StDev,
    double? GMean,
    double? Jitter,
    double? JAvg,
    double? JMax,
    double? JInt,
    string Status,
    double?[] History,
    bool IsDestination,
    int ConsecutiveLoss);

public sealed record TcpStatsDto(int Port, int Sent, int Recv, double Loss, double? Last, double? Best, double? Avg, double? Worst, string Status);

public sealed record NoteDto(string Level, string Code, string Text, int? Ttl);

public sealed record HopsSnapshotDto(string SessionId, int Round, int? DestTtl, List<HopDto> Hops, TcpStatsDto? Tcp, List<NoteDto> Notes, long ElapsedMs, object? Vpn = null);

public sealed record ProbeRowDto(
    long T,
    int Round,
    long Seq,
    int Ttl,
    string? Src,
    string Dst,
    string? From,
    string Status,
    uint RawStatus,
    double? RttMs,
    uint KernelMs,
    int Size,
    string Proto);

public sealed record TraceEventDto(long T, string Level, string Code, string Message, int? Ttl, string? Ip);

public sealed class TraceStateDto
{
    public required string SessionId { get; init; }
    public required string State { get; set; }
    public required string Input { get; init; }
    public string? Host { get; set; }
    public string? TargetIp { get; set; }
    public string? SourceIp { get; set; }
    public string? AdapterId { get; init; }
    public string? Family { get; set; }
    public long StartedAt { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public long? OutageSince { get; set; }
    public int? Port { get; set; }
    /// <summary>normal | vpn</summary>
    public string Mode { get; set; } = "normal";
    /// <summary>VPN mode: the real destination behind the tunnel (Input is then the VPN server).</summary>
    public string? OverlayInput { get; set; }
    public string? OverlayIp { get; set; }
    public string? CarrierName { get; set; }
}

public static class Num
{
    public static double? N(double v, int digits = 3) => double.IsNaN(v) || double.IsInfinity(v) ? null : Math.Round(v, digits);
}
