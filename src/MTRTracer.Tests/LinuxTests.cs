using System.Buffers.Binary;
using System.Net;
using MTRTracer.Core.Native;
using MTRTracer.Core.Probing;

namespace MTRTracer.Tests;

public class LinuxParsingTests
{
    [Fact]
    public void RouteGetWithGateway()
    {
        var r = LinuxSystem.ParseRouteGet("1.1.1.1 via 10.0.0.1 dev enp3s0 src 10.0.0.2 uid 1000 \n    cache \n")!.Value;
        Assert.Equal(IPAddress.Parse("10.0.0.1"), r.Via);
        Assert.Equal("enp3s0", r.Dev);
        Assert.Equal(IPAddress.Parse("10.0.0.2"), r.Src);
    }

    [Fact]
    public void RouteGetDirectAndTunnel()
    {
        var r = LinuxSystem.ParseRouteGet("1.1.1.1 dev tun0 table 2022 src 172.19.0.1 uid 1000")!.Value;
        Assert.Null(r.Via);
        Assert.Equal("tun0", r.Dev);
        Assert.Null(LinuxSystem.ParseRouteGet("unreachable 10.9.9.9 uid 1000"));
        Assert.Null(LinuxSystem.ParseRouteGet(""));
    }

    [Fact]
    public void ProcNetTcp4()
    {
        const string text = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 0100007F:0277 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 23456 1 0000000000000000 100 0 0 10 0
               1: 0200000A:C350 0100000A:01BB 01 00000000:00000000 02:000A7C2E 00000000  1000        0 98765 2 0000000000000000 20 4 30 10 -1
            """;
        var rows = LinuxSystem.ParseProcNet(text, v6: false);
        Assert.Equal(2, rows.Count);
        Assert.Equal(IPAddress.Parse("127.0.0.1"), rows[0].Local);
        Assert.Equal(631, rows[0].LocalPort);
        Assert.Equal(10, rows[0].State); // LISTEN
        Assert.Equal(IPAddress.Parse("10.0.0.2"), rows[1].Local);
        Assert.Equal(50000, rows[1].LocalPort);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), rows[1].Remote);
        Assert.Equal(443, rows[1].RemotePort);
        Assert.Equal(1, rows[1].State); // ESTABLISHED
        Assert.Equal(98765, rows[1].Inode);
    }

    [Fact]
    public void ProcNetTcp6()
    {
        // 2606:4700:4700::1111 port 443, and ::ffff:10.0.0.2 (v4-mapped).
        const string text = """
              sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 0000000000000000FFFF00000200000A:A410 00470626000000470000000011110000:01BB 01 00000000:00000000 00:00000000 00000000  1000        0 555 1 0000000000000000 20 4 0 10 -1
            """;
        var r = LinuxSystem.ParseProcNet(text, v6: true).Single();
        Assert.Equal(IPAddress.Parse("10.0.0.2"), r.Local);
        Assert.Equal(42000, r.LocalPort);
        Assert.Equal(IPAddress.Parse("2606:4700:4700::1111"), r.Remote);
        Assert.Equal(443, r.RemotePort);
    }

    [Fact]
    public void ClassifiesInterfaceNames()
    {
        Assert.Equal("loopback", LinuxSystem.Classify("lo"));
        Assert.Equal("ethernet", LinuxSystem.Classify("enp3s0"));
        Assert.Equal("ethernet", LinuxSystem.Classify("eth0"));
        Assert.Equal("wifi", LinuxSystem.Classify("wlp2s0"));
        Assert.Equal("vpn", LinuxSystem.Classify("tun0"));
        Assert.Equal("vpn", LinuxSystem.Classify("wg0"));
        Assert.Equal("vpn", LinuxSystem.Classify("tailscale0"));
        Assert.Equal("virtual", LinuxSystem.Classify("docker0"));
        Assert.Equal("virtual", LinuxSystem.Classify("br-1a2b3c"));
        Assert.Equal("virtual", LinuxSystem.Classify("veth12ab"));
    }
}

public class LinuxIcmpTests
{
    /// <summary>Builds a cmsghdr carrying sock_extended_err + offender sockaddr_in, as the kernel does.</summary>
    private static byte[] Control(int level, int type, byte origin, byte icmpType, byte icmpCode, IPAddress offender)
    {
        var buf = new byte[16 + 16 + 16];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, (ulong)(16 + 16 + 16)); // cmsg_len
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), level);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12), type);
        var d = buf.AsSpan(16);
        BinaryPrimitives.WriteUInt32LittleEndian(d, 113); // EHOSTUNREACH
        d[4] = origin;
        d[5] = icmpType;
        d[6] = icmpCode;
        var sa = d[16..];
        BinaryPrimitives.WriteUInt16LittleEndian(sa, 2); // AF_INET
        offender.GetAddressBytes().CopyTo(sa[4..]);
        return buf;
    }

    [Fact]
    public void ParsesTtlExceededFromErrorQueue()
    {
        var ctl = Control(0, 11, 2, 11, 0, IPAddress.Parse("198.51.100.10"));
        var e = LinuxIcmpProber.ParseControl(ctl)!.Value;
        Assert.Equal(2, e.Origin);
        Assert.Equal(11, e.Type);
        Assert.Equal(IPAddress.Parse("198.51.100.10"), e.Offender);
        Assert.Equal(ProbeStatus.TtlExpired, LinuxIcmpProber.MapIcmp4(e.Type, e.Code));
    }

    [Fact]
    public void IgnoresUnrelatedControlMessages()
    {
        var other = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(other, 20);
        BinaryPrimitives.WriteInt32LittleEndian(other.AsSpan(8), 1); // SOL_SOCKET
        BinaryPrimitives.WriteInt32LittleEndian(other.AsSpan(12), 29); // SO_TIMESTAMP
        var err = Control(0, 11, 2, 3, 13, IPAddress.Parse("10.0.0.1"));
        var both = other.Concat(err).ToArray();
        var e = LinuxIcmpProber.ParseControl(both)!.Value;
        Assert.Equal(ProbeStatus.Prohibited, LinuxIcmpProber.MapIcmp4(e.Type, e.Code));
    }

    [Theory]
    [InlineData(11, 0, ProbeStatus.TtlExpired)]
    [InlineData(3, 0, ProbeStatus.NetUnreachable)]
    [InlineData(3, 1, ProbeStatus.HostUnreachable)]
    [InlineData(3, 3, ProbeStatus.PortUnreachable)]
    [InlineData(3, 4, ProbeStatus.PacketTooBig)]
    [InlineData(3, 13, ProbeStatus.Prohibited)]
    public void MapsIcmp4(byte type, byte code, ProbeStatus expected) => Assert.Equal(expected, LinuxIcmpProber.MapIcmp4(type, code));

    [Theory]
    [InlineData(3, 0, ProbeStatus.TtlExpired)]
    [InlineData(1, 0, ProbeStatus.NetUnreachable)]
    [InlineData(1, 1, ProbeStatus.Prohibited)]
    [InlineData(1, 4, ProbeStatus.PortUnreachable)]
    [InlineData(2, 0, ProbeStatus.PacketTooBig)]
    public void MapsIcmp6(byte type, byte code, ProbeStatus expected) => Assert.Equal(expected, LinuxIcmpProber.MapIcmp6(type, code));

    [Fact]
    public void ChecksumMatchesRfc1071Example()
    {
        // Echo request id=1 seq=1, no payload: checksum must make the ones' complement sum 0xFFFF.
        var p = new byte[] { 8, 0, 0, 0, 0, 1, 0, 1 };
        var c = LinuxIcmpProber.Checksum(p);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), c);
        Assert.Equal(0, LinuxIcmpProber.Checksum(p));
        Assert.Equal(0xF7FD, c);
    }

    [Fact]
    public void RawStatusesMatchWindowsVocabulary()
    {
        Assert.Equal(11013u, LinuxIcmpProber.Raw(ProbeStatus.TtlExpired));
        Assert.Equal(ProbeStatus.TtlExpired, IcmpProber.MapStatus(LinuxIcmpProber.Raw(ProbeStatus.TtlExpired)));
        Assert.Equal(ProbeStatus.PacketTooBig, IcmpProber.MapStatus(LinuxIcmpProber.Raw(ProbeStatus.PacketTooBig)));
    }
}

public class LinuxIcmpLoopbackTests
{
    /// <summary>Real ICMP echo over loopback (never leaves the machine). Runs only on Linux.</summary>
    [Fact]
    public void EchoToLoopback()
    {
        if (!OperatingSystem.IsLinux()) return;
        IIcmpProber p;
        try
        {
            p = IcmpProber.Create(System.Net.Sockets.AddressFamily.InterNetwork, 128);
        }
        catch (UnauthorizedAccessException)
        {
            return; // CI runner without ping sockets or CAP_NET_RAW: nothing to test here.
        }
        using var _ = p;
        var r = p.Send(IPAddress.Loopback, null, 64, 1000, IcmpProber.BuildPayload(32, "MTRTracer"), false);
        Assert.Equal(ProbeStatus.Ok, r.Status);
        Assert.Equal(IPAddress.Loopback, r.From);
        Assert.InRange(r.RttMs, 0, 50);

        // Several probes in a row on the same socket keep matching their own sequence numbers.
        for (var i = 0; i < 5; i++)
            Assert.Equal(ProbeStatus.Ok, p.Send(IPAddress.Loopback, null, 64, 1000, new byte[16], i % 2 == 0).Status);
    }

    [Fact]
    public void ReadsOwnSocketsFromProc()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var rows = LinuxSystem.ReadConnections();
        var mine = rows.FirstOrDefault(r => r.Proto == "tcp" && r.LocalPort == port);
        Assert.NotNull(mine);
        Assert.Equal("LISTEN", mine!.State);
        Assert.Equal(Environment.ProcessId, mine.Pid);
    }

    [Fact]
    public void RouteLookupForLoopback()
    {
        if (!OperatingSystem.IsLinux() || LinuxSystem.Run("ip", ["-V"], 2000) == null) return;
        var r = LinuxSystem.LookupRoute(IPAddress.Loopback, 0, null);
        Assert.NotNull(r);
    }
}
