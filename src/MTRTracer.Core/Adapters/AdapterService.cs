using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MTRTracer.Core.Native;

namespace MTRTracer.Core.Adapters;

public sealed record IpAddrInfo(string Address, int Prefix);

public sealed class AdapterInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Kind { get; init; }
    public required string Status { get; init; }
    public string? Mac { get; init; }
    public long SpeedBps { get; init; }
    public int? Mtu { get; init; }
    public int IfIndex { get; init; }
    public int Ipv6IfIndex { get; init; }
    public required string IfType { get; init; }
    public List<IpAddrInfo> Ipv4 { get; init; } = [];
    public List<IpAddrInfo> Ipv6 { get; init; } = [];
    public List<string> Gateways { get; init; } = [];
    public List<string> Dns { get; init; } = [];
    public string? DnsSuffix { get; init; }
    public bool Dhcp { get; init; }
    public bool HasGateway { get; init; }
    public bool HasRoute { get; init; }
    public bool HasRouteV6 { get; init; }
    public bool IsWindowsChoice { get; set; }
    public bool IsWindowsChoiceV6 { get; set; }
    public int? RouteMetric { get; set; }
    public long BytesSent { get; init; }
    public long BytesRecv { get; init; }
}

public readonly record struct RouteInfo(int IfIndex, IPAddress? NextHop, IPAddress? Source, int Metric);

public static class AdapterService
{
    public static readonly IPAddress ProbeV4 = IPAddress.Parse("1.1.1.1");
    public static readonly IPAddress ProbeV6 = IPAddress.Parse("2606:4700:4700::1111");

    private static readonly System.Text.RegularExpressions.Regex FilterBinding = new(@"-\d{4}$");

    public static List<AdapterInfo> List()
    {
        var bestV4 = LookupRoute(ProbeV4);
        var bestV6 = LookupRoute(ProbeV6);
        var list = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                // NDIS filter bindings (QoS, WFP, Npcap) show up as "<adapter>-<filter>-0000" clones.
                if (ni.OperationalStatus == OperationalStatus.NotPresent || FilterBinding.IsMatch(ni.Name)) continue;
                var info = Build(ni);
                if (info == null) continue;
                if (bestV4 is { } r4 && r4.IfIndex == info.IfIndex && info.IfIndex != 0)
                {
                    info.IsWindowsChoice = true;
                    info.RouteMetric = r4.Metric;
                }
                if (bestV6 is { } r6 && r6.IfIndex == info.Ipv6IfIndex && info.Ipv6IfIndex != 0)
                    info.IsWindowsChoiceV6 = true;
                list.Add(info);
            }
            catch
            {
                // Some virtual adapters throw on property access; skip them.
            }
        }
        return list
            .OrderByDescending(a => a.IsWindowsChoice)
            .ThenByDescending(a => a.Status == "up")
            .ThenByDescending(a => a.HasRoute)
            .ThenBy(a => a.Kind == "loopback")
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static NetworkInterface? Find(string id) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == id);

    private static AdapterInfo? Build(NetworkInterface ni)
    {
        var props = ni.GetIPProperties();
        int ifIndex = 0, ifIndex6 = 0, mtu = 0;
        try { var p4 = props.GetIPv4Properties(); if (p4 != null) { ifIndex = p4.Index; mtu = p4.Mtu; } } catch { }
        try { var p6 = props.GetIPv6Properties(); if (p6 != null) { ifIndex6 = p6.Index; if (mtu == 0) mtu = p6.Mtu; } } catch { }
        bool dhcp = false;
        try { dhcp = props.GetIPv4Properties()?.IsDhcpEnabled ?? false; } catch { }

        var v4 = new List<IpAddrInfo>();
        var v6 = new List<IpAddrInfo>();
        foreach (var ua in props.UnicastAddresses)
        {
            var item = new IpAddrInfo(Format(ua.Address), ua.PrefixLength);
            if (ua.Address.AddressFamily == AddressFamily.InterNetwork) v4.Add(item);
            else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6) v6.Add(item);
        }
        var gateways = props.GatewayAddresses
            .Select(g => g.Address)
            .Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))
            .Select(Format).ToList();

        long sent = 0, recv = 0;
        try { var st = ni.GetIPStatistics(); sent = st.BytesSent; recv = st.BytesReceived; } catch { }

        string? mac = null;
        try
        {
            var bytes = ni.GetPhysicalAddress().GetAddressBytes();
            if (bytes.Length > 0) mac = string.Join(":", bytes.Select(b => b.ToString("X2")));
        }
        catch { }

        return new AdapterInfo
        {
            Id = ni.Id,
            Name = ni.Name,
            Description = ni.Description,
            Kind = Classify(ni),
            Status = ni.OperationalStatus switch
            {
                OperationalStatus.Up => "up",
                OperationalStatus.Down => "down",
                OperationalStatus.Dormant => "dormant",
                OperationalStatus.NotPresent => "not-present",
                _ => "other",
            },
            Mac = mac,
            SpeedBps = ni.Speed,
            Mtu = mtu > 0 ? mtu : null,
            IfIndex = ifIndex,
            Ipv6IfIndex = ifIndex6,
            IfType = ni.NetworkInterfaceType.ToString(),
            Ipv4 = v4,
            Ipv6 = v6,
            Gateways = gateways,
            Dns = props.DnsAddresses.Select(Format).ToList(),
            DnsSuffix = string.IsNullOrWhiteSpace(props.DnsSuffix) ? null : props.DnsSuffix,
            Dhcp = dhcp,
            HasGateway = gateways.Count > 0,
            HasRoute = ifIndex != 0 && v4.Count > 0 && LookupRoute(ProbeV4, ifIndex) != null,
            HasRouteV6 = ifIndex6 != 0 && v6.Count > 0 && LookupRoute(ProbeV6, ifIndex6) != null,
            BytesSent = sent,
            BytesRecv = recv,
        };
    }

    private static readonly string[] VpnHints =
    [
        "wintun", "wireguard", "tap-windows", "tap-", "openvpn", "vpn", "tunnel", "fortinet", "anyconnect",
        "zerotier", "tailscale", "nordlynx", "proton", "outline", "amnezia", "hamachi", "radmin", "softether",
        "pangp", "juniper", "sstp", "l2tp", "pptp", "ikev2", "wan miniport", "cloudflare warp", "warp",
    ];

    private static readonly string[] VirtualHints =
    [
        "hyper-v", "vethernet", "virtualbox", "vmware", "wsl", "docker", "npcap", "virtual", "loopback", "bluetooth",
        "kernel debug", "teredo", "isatap", "6to4", "pseudo",
    ];

    public static string Classify(NetworkInterface ni)
    {
        if (!OperatingSystem.IsWindows()) return LinuxSystem.Classify(ni.Name);
        var d = (ni.Description + " " + ni.Name).ToLowerInvariant();
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) return "loopback";
        if (VpnHints.Any(d.Contains) || ni.NetworkInterfaceType is NetworkInterfaceType.Ppp) return "vpn";
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return d.Contains("teredo") || d.Contains("isatap") || d.Contains("6to4") ? "virtual" : "vpn";
        if (VirtualHints.Any(d.Contains)) return "virtual";
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "wifi";
        if (ni.NetworkInterfaceType is NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2) return "cellular";
        if (ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit)
            return "ethernet";
        return "other";
    }

    /// <summary>
    /// Asks Windows which route (interface, next hop, source address) it would use for a destination.
    /// With <paramref name="ifIndex"/> set the lookup is restricted to that interface.
    /// </summary>
    public static RouteInfo? LookupRoute(IPAddress destination, int ifIndex = 0, IPAddress? source = null)
    {
        if (!OperatingSystem.IsWindows()) return LinuxSystem.LookupRoute(destination, ifIndex, source);
        try
        {
            var dst = IpHlpApi.ToSockAddr(destination, destination.AddressFamily);
            var src = source == null ? null : IpHlpApi.ToSockAddr(source, source.AddressFamily);
            var row = new byte[256];
            var best = new byte[IpHlpApi.SockAddrInetSize];
            var rc = IpHlpApi.GetBestRoute2(IntPtr.Zero, (uint)ifIndex, src, dst, 0, row, best);
            if (rc != 0) return null;
            // MIB_IPFORWARD_ROW2: Luid(8) IfIndex(4) DestinationPrefix(32) NextHop(28) ... Metric @84
            var idx = (int)BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(8, 4));
            var nextHop = IpHlpApi.FromSockAddr(row.AsSpan(44, IpHlpApi.SockAddrInetSize));
            var metric = (int)BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(84, 4));
            var bestSource = IpHlpApi.FromSockAddr(best);
            return new RouteInfo(idx, nextHop, bestSource, metric);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Picks the source address to bind probes to. Returns null for "auto" (let Windows route).
    /// Throws with a user facing message when the adapter cannot reach the given family.
    /// </summary>
    public static IPAddress? ResolveSource(string? adapterId, IPAddress destination)
    {
        if (string.IsNullOrEmpty(adapterId)) return null;
        var ni = Find(adapterId) ?? throw new AdapterException("adapter-missing", "Выбранный адаптер больше не найден в системе.");
        if (ni.OperationalStatus != OperationalStatus.Up)
            throw new AdapterException("adapter-down", $"Адаптер «{ni.Name}» отключён.");
        var family = destination.AddressFamily;
        var candidates = ni.GetIPProperties().UnicastAddresses
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == family)
            .ToList();
        if (candidates.Count == 0)
            throw new AdapterException("no-address", family == AddressFamily.InterNetworkV6
                ? $"У адаптера «{ni.Name}» нет IPv6-адреса."
                : $"У адаптера «{ni.Name}» нет IPv4-адреса.");
        if (family == AddressFamily.InterNetworkV6)
        {
            // Prefer global unicast over link-local / temporary scoped addresses.
            return candidates.FirstOrDefault(a => !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal && !a.IsIPv6UniqueLocal)
                   ?? candidates[0];
        }
        return candidates.FirstOrDefault(a => !a.ToString().StartsWith("169.254.")) ?? candidates[0];
    }

    /// <summary>Source address Windows itself would use (UDP connect trick: no packet is sent).</summary>
    public static IPAddress? SystemSourceFor(IPAddress destination)
    {
        try
        {
            using var s = new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(destination, 9));
            return (s.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch
        {
            return null;
        }
    }

    public static string? AdapterIdForAddress(IPAddress local)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (ni.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(local))) return ni.Id;
            }
            catch { }
        }
        return null;
    }

    public static string Format(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        return a.ToString();
    }
}

public sealed class AdapterException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
