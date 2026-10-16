using System.Net;
using MTRTracer.Core.Connections;
using MTRTracer.Core.Geo;

namespace MTRTracer.Core.Adapters;

public sealed record TunnelServerCandidate(string Ip, int Port, int Connections);

public sealed record TunnelInfo(
    string AdapterId,
    string AdapterName,
    bool IsDefaultRoute,
    string? Process,
    int? Pid,
    string? ProcessPath,
    List<TunnelServerCandidate> Servers,
    string? CarrierId,
    string? CarrierName,
    string? CarrierIp,
    string Explanation);

/// <summary>
/// Understands proxy/VPN tunnels: which client owns the tunnel, which server it talks to, and which physical
/// adapter actually carries the encrypted traffic. Proxy tunnels (Xray, sing-box, Clash, tun2socks…) forward
/// TCP/UDP streams only, so hop-by-hop tracing through them is impossible; the server is reachable directly.
/// </summary>
public static class TunnelDetector
{
    private static readonly string[] Clients =
    [
        "xray", "v2ray", "sing-box", "singbox", "mihomo", "clash", "verge", "hiddify", "nekoray", "nekobox", "v2rayn",
        "wireguard", "openvpn", "amnezia", "outline", "tun2socks", "happ", "karing", "throne", "shadowsocks", "ss-local",
        "trojan", "hysteria", "tuic", "naive", "warp", "protonvpn", "nordvpn", "windscribe", "psiphon", "lantern", "v2box",
    ];

    public static bool IsClientProcess(string name)
    {
        var n = name.ToLowerInvariant();
        return Clients.Any(n.Contains);
    }

    public static TunnelInfo? Detect(string tunnelAdapterId)
    {
        var adapters = AdapterService.List();
        var tunnel = adapters.FirstOrDefault(a => a.Id == tunnelAdapterId);
        if (tunnel == null) return null;

        // Physical adapters: real NICs with a gateway and a route to the internet.
        var physical = adapters
            .Where(a => a.Id != tunnel.Id && a.Status == "up" && a.HasGateway && a.Kind is "ethernet" or "wifi" or "cellular" or "other")
            .ToList();
        var localToAdapter = new Dictionary<string, AdapterInfo>();
        foreach (var a in physical)
            foreach (var ip in a.Ipv4.Concat(a.Ipv6))
                localToAdapter[ip.Address.Split('%')[0]] = a;

        var conns = ConnectionMonitor.ReadAll();
        // Sockets of a known VPN/proxy client that leave from a physical adapter to a public address.
        var byPid = conns
            .Where(c => c.Proto == "tcp" && c.State == "ESTABLISHED" && c.Remote != null && IpClassifier.IsPublic(c.Remote)
                        && localToAdapter.ContainsKey(AdapterService.Format(c.Local)))
            .GroupBy(c => c.Pid)
            .Select(g => (Pid: g.Key, Info: ConnectionMonitor.ProcessInfo(g.Key), Conns: g.ToList()))
            .Where(x => IsClientProcess(x.Info.Name))
            .OrderByDescending(x => x.Conns.Count)
            .ToList();

        var owner = byPid.FirstOrDefault();
        var servers = owner.Conns == null
            ? []
            : owner.Conns
                .GroupBy(c => (Ip: AdapterService.Format(c.Remote!), Port: c.RemotePort ?? 0))
                .Select(g => new TunnelServerCandidate(g.Key.Ip, g.Key.Port, g.Count()))
                .OrderByDescending(s => s.Connections)
                .Take(8)
                .ToList();

        AdapterInfo? carrier = null;
        if (servers.Count > 0 && IPAddress.TryParse(servers[0].Ip, out var serverIp)
            && AdapterService.LookupRoute(serverIp) is { } route)
            carrier = physical.FirstOrDefault(a => a.IfIndex == route.IfIndex || a.Ipv6IfIndex == route.IfIndex);
        if (carrier == null && owner.Conns is { Count: > 0 })
            carrier = localToAdapter.GetValueOrDefault(AdapterService.Format(owner.Conns[0].Local));
        carrier ??= physical.FirstOrDefault();

        var explanation = carrier != null
            ? $"Туннель «{tunnel.Name}» не имеет своего провода: зашифрованный трафик физически уходит через «{carrier.Name}». Поэтому счётчики {carrier.Name} всегда больше."
            : $"Туннель «{tunnel.Name}»: физический адаптер не найден.";

        return new TunnelInfo(tunnel.Id, tunnel.Name, tunnel.IsWindowsChoice, owner.Info.Name, owner.Conns == null ? null : owner.Pid,
            owner.Info.Path, servers, carrier?.Id, carrier?.Name, carrier?.Ipv4.FirstOrDefault()?.Address, explanation);
    }
}
