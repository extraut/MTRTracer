using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MTRTracer.Core.Adapters;
using MTRTracer.Core.Connections;

namespace MTRTracer.Core.Native;

/// <summary>Linux counterparts of the Windows IP Helper functions: routes, sockets, processes, interfaces.</summary>
public static class LinuxSystem
{
    // ---------------- routes: `ip route get` ----------------

    public readonly record struct RouteGet(IPAddress? Via, string? Dev, IPAddress? Src, int Metric);

    /// <summary>Parses one line of `ip -o route get` output, e.g. "1.1.1.1 via 10.0.0.1 dev eth0 src 10.0.0.2 uid 1000".</summary>
    public static RouteGet? ParseRouteGet(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(line) || line.StartsWith("unreachable") || line.StartsWith("prohibit") || line.StartsWith("blackhole")) return null;
        var t = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        IPAddress? via = null, src = null;
        string? dev = null;
        var metric = 0;
        for (var i = 0; i < t.Length - 1; i++)
        {
            switch (t[i])
            {
                case "via": IPAddress.TryParse(t[i + 1], out via); break;
                case "dev": dev = t[i + 1]; break;
                case "src": IPAddress.TryParse(t[i + 1], out src); break;
                case "metric": int.TryParse(t[i + 1], out metric); break;
            }
        }
        return dev == null ? null : new RouteGet(via, dev, src, metric);
    }

    public static RouteInfo? LookupRoute(IPAddress destination, int ifIndex, IPAddress? source)
    {
        var args = new List<string> { "-o", destination.AddressFamily == AddressFamily.InterNetworkV6 ? "-6" : "-4", "route", "get", destination.ToString() };
        if (source != null) args.AddRange(["from", source.ToString()]);
        if (ifIndex != 0 && InterfaceByIndex(ifIndex) is { } name) args.AddRange(["oif", name]);
        var output = Run("ip", args, 2000);
        if (output == null || ParseRouteGet(output) is not { } r) return null;
        var idx = 0;
        try
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == r.Dev);
            var props = ni?.GetIPProperties();
            idx = destination.AddressFamily == AddressFamily.InterNetworkV6 ? props?.GetIPv6Properties()?.Index ?? 0 : props?.GetIPv4Properties()?.Index ?? 0;
        }
        catch { }
        return new RouteInfo(idx, r.Via, r.Src, r.Metric);
    }

    private static string? InterfaceByIndex(int index)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var p = ni.GetIPProperties();
                if (p.GetIPv4Properties()?.Index == index || p.GetIPv6Properties()?.Index == index) return ni.Name;
            }
            catch { }
        }
        return null;
    }

    public static string? Run(string file, IEnumerable<string> args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return null;
            }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------------- interfaces ----------------

    /// <summary>Kind of a Linux interface from its name and sysfs.</summary>
    public static string Classify(string name)
    {
        var n = name.ToLowerInvariant();
        if (n == "lo") return "loopback";
        if (n.StartsWith("tun") || n.StartsWith("tap") || n.StartsWith("wg") || n.StartsWith("ppp") || n.StartsWith("tailscale")
            || n.StartsWith("zt") || n.StartsWith("nordlynx") || n.StartsWith("proton") || n.StartsWith("utun") || n.Contains("vpn")
            || n.StartsWith("sing") || n.StartsWith("xray") || n.StartsWith("clash") || n.StartsWith("mihomo") || n.StartsWith("amn")) return "vpn";
        if (n.StartsWith("docker") || n.StartsWith("br-") || n.StartsWith("veth") || n.StartsWith("virbr") || n.StartsWith("vmnet")
            || n.StartsWith("lxc") || n.StartsWith("lxd") || n.StartsWith("cni") || n.StartsWith("flannel") || n.StartsWith("cali")
            || n.StartsWith("vboxnet") || n.StartsWith("podman")) return "virtual";
        if (n.StartsWith("wl") || Directory.Exists($"/sys/class/net/{name}/wireless")) return "wifi";
        if (n.StartsWith("wwan") || n.StartsWith("rmnet") || n.StartsWith("usb")) return "cellular";
        if (n.StartsWith("en") || n.StartsWith("eth")) return "ethernet";
        return "other";
    }

    // ---------------- sockets: /proc/net/{tcp,tcp6,udp,udp6} ----------------

    private static readonly string[] TcpStates =
        ["?", "ESTABLISHED", "SYN_SENT", "SYN_RCVD", "FIN_WAIT1", "FIN_WAIT2", "TIME_WAIT", "CLOSED", "CLOSE_WAIT", "LAST_ACK", "LISTEN", "CLOSING"];

    public readonly record struct ProcNetRow(IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, int State, long Inode);

    /// <summary>Parses a /proc/net/tcp|udp table. Addresses are __be32 words printed as host-order hex.</summary>
    public static List<ProcNetRow> ParseProcNet(string text, bool v6)
    {
        var rows = new List<ProcNetRow>();
        foreach (var raw in text.Split('\n').Skip(1))
        {
            var t = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 10) continue;
            if (!TryEndpoint(t[1], v6, out var la, out var lp) || !TryEndpoint(t[2], v6, out var ra, out var rp)) continue;
            var state = int.Parse(t[3], NumberStyles.HexNumber);
            long.TryParse(t[9], out var inode);
            rows.Add(new ProcNetRow(la, lp, ra, rp, state, inode));
        }
        return rows;
    }

    private static bool TryEndpoint(string s, bool v6, out IPAddress addr, out int port)
    {
        addr = IPAddress.Any;
        port = 0;
        var c = s.IndexOf(':');
        if (c < 0) return false;
        var hex = s[..c];
        port = int.Parse(s[(c + 1)..], NumberStyles.HexNumber);
        var bytes = new byte[v6 ? 16 : 4];
        for (var w = 0; w < bytes.Length / 4; w++)
        {
            var word = uint.Parse(hex.Substring(w * 8, 8), NumberStyles.HexNumber);
            // The kernel printed the in-memory (network order) bytes as a little-endian u32.
            bytes[w * 4] = (byte)word;
            bytes[w * 4 + 1] = (byte)(word >> 8);
            bytes[w * 4 + 2] = (byte)(word >> 16);
            bytes[w * 4 + 3] = (byte)(word >> 24);
        }
        addr = new IPAddress(bytes);
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        return true;
    }

    private static Dictionary<long, int> _inodes = new();
    private static long _inodesAt;

    /// <summary>socket inode → PID, from /proc/[pid]/fd links (only processes we may inspect).</summary>
    private static Dictionary<long, int> InodeMap()
    {
        if (Environment.TickCount64 - _inodesAt < 2000) return _inodes;
        var map = new Dictionary<long, int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                foreach (var fd in Directory.EnumerateFiles(Path.Combine(dir, "fd")))
                {
                    var target = new FileInfo(fd).LinkTarget;
                    if (target != null && target.StartsWith("socket:[") && long.TryParse(target.AsSpan(8, target.Length - 9), out var inode))
                        map[inode] = pid;
                }
            }
            catch
            {
                // Other users' processes: not visible without root.
            }
        }
        _inodes = map;
        _inodesAt = Environment.TickCount64;
        return map;
    }

    public static List<RawConnection> ReadConnections()
    {
        var pids = InodeMap();
        var list = new List<RawConnection>();
        void Read(string file, string proto, bool v6)
        {
            string text;
            try { text = File.ReadAllText(file); } catch { return; }
            foreach (var r in ParseProcNet(text, v6))
            {
                var pid = r.Inode != 0 && pids.TryGetValue(r.Inode, out var p) ? p : 0;
                var remoteSet = !(r.Remote.Equals(IPAddress.Any) || r.Remote.Equals(IPAddress.IPv6Any)) || r.RemotePort != 0;
                if (proto == "tcp")
                {
                    var state = r.State is > 0 and < 12 ? TcpStates[r.State] : "?";
                    var listening = r.State == 10;
                    list.Add(new RawConnection("tcp", r.Local, r.LocalPort, listening ? null : r.Remote, listening ? null : r.RemotePort, state, pid));
                }
                else
                {
                    list.Add(new RawConnection("udp", r.Local, r.LocalPort, remoteSet ? r.Remote : null, remoteSet ? r.RemotePort : null, remoteSet ? "CONNECTED" : "BOUND", pid));
                }
            }
        }
        Read("/proc/net/tcp", "tcp", false);
        Read("/proc/net/tcp6", "tcp", true);
        Read("/proc/net/udp", "udp", false);
        Read("/proc/net/udp6", "udp", true);
        return list;
    }

    // ---------------- processes ----------------

    public static (string Name, string? Path) ProcessInfo(int pid)
    {
        if (pid <= 0) return (Loc.S("другой пользователь", "another user"), null);
        string name = $"PID {pid}";
        string? path = null;
        try { name = File.ReadAllText($"/proc/{pid}/comm").Trim(); } catch { }
        try { path = new FileInfo($"/proc/{pid}/exe").LinkTarget; } catch { }
        return (name, path);
    }
}
