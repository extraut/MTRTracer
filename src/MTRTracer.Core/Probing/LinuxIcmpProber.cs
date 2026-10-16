using MTRTracer.Core;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MTRTracer.Core.Probing;

/// <summary>
/// Linux ICMP prober.
/// Preferred mode: unprivileged "ping sockets" (SOCK_DGRAM + IPPROTO_ICMP), allowed for the user's group by
/// net.ipv4.ping_group_range (open by default on Ubuntu, Fedora, Debian 12+, Arch). Replies to our echo come
/// back on the socket; "TTL exceeded" / "unreachable" from routers arrive on the socket error queue
/// (IP_RECVERR, read with recvmsg(MSG_ERRQUEUE)) together with the offending router's address.
/// Fallback: a raw ICMP socket (needs CAP_NET_RAW: root or `setcap cap_net_raw+ep`), parsing quoted headers.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class LinuxIcmpProber : IIcmpProber
{
    public static string PermissionHelp => Loc.S(
        "Нет прав на ICMP. Разрешите непривилегированный ping: sudo sysctl -w net.ipv4.ping_group_range=\"0 2147483647\" " +
        "(и net.ipv6 аналогично), либо дайте программе право на raw-сокеты: sudo setcap cap_net_raw+ep ./MTRTracer",
        "No permission for ICMP. Allow unprivileged ping: sudo sysctl -w net.ipv4.ping_group_range=\"0 2147483647\" " +
        "(and the same for net.ipv6), or give the program raw-socket rights: sudo setcap cap_net_raw+ep ./MTRTracer");

    private const int SOL_IP = 0, IP_TTL = 2, IP_MTU_DISCOVER = 10, IP_RECVERR = 11, IP_PMTUDISC_DONT = 0, IP_PMTUDISC_DO = 2;
    private const int SOL_IPV6 = 41, IPV6_UNICAST_HOPS = 16, IPV6_MTU_DISCOVER = 23, IPV6_RECVERR = 25;
    private const int MSG_DONTWAIT = 0x40, MSG_ERRQUEUE = 0x2000;
    private const short POLLIN = 1, POLLERR = 8;
    private const byte SO_EE_ORIGIN_LOCAL = 1, SO_EE_ORIGIN_ICMP = 2, SO_EE_ORIGIN_ICMP6 = 3;
    private const int EMSGSIZE = 90;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short REvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec
    {
        public void* Base;
        public nuint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsgHdr
    {
        public void* Name;
        public uint NameLen;
        public IoVec* Iov;
        public nuint IovLen;
        public void* Control;
        public nuint ControlLen;
        public int Flags;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(PollFd* fds, nuint nfds, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern nint recvmsg(int sockfd, MsgHdr* msg, int flags);

    private readonly AddressFamily _family;
    private readonly bool _raw;
    private readonly ushort _ident = (ushort)Random.Shared.Next(1, ushort.MaxValue);
    private readonly byte[] _rx = new byte[70_000];
    private readonly byte[] _ctl = new byte[512];
    private readonly byte[] _name = new byte[128];
    private Socket _socket;
    private IPAddress? _bound;
    private ushort _seq;
    private int _ttl = -1;
    private int _df = -1;

    public bool IsRaw => _raw;

    private LinuxIcmpProber(AddressFamily family, bool raw)
    {
        _family = family;
        _raw = raw;
        _socket = Open(null);
    }

    /// <summary>Unprivileged ping socket if permitted, raw socket otherwise, or a clear error.</summary>
    public static IIcmpProber Create(AddressFamily family, int maxPayload)
    {
        try
        {
            return new LinuxIcmpProber(family, raw: false);
        }
        catch (SocketException)
        {
            try
            {
                return new LinuxIcmpProber(family, raw: true);
            }
            catch (SocketException)
            {
                throw new UnauthorizedAccessException(PermissionHelp);
            }
        }
    }

    private bool V6 => _family == AddressFamily.InterNetworkV6;

    private Socket Open(IPAddress? source)
    {
        var s = new Socket(_family, _raw ? SocketType.Raw : SocketType.Dgram, V6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp)
        {
            Blocking = false,
        };
        try
        {
            if (!_raw) s.SetRawSocketOption(V6 ? SOL_IPV6 : SOL_IP, V6 ? IPV6_RECVERR : IP_RECVERR, Int(1));
            if (source != null) s.Bind(new IPEndPoint(source, 0));
        }
        catch
        {
            s.Dispose();
            throw;
        }
        _bound = source;
        _ttl = -1;
        _df = -1;
        return s;
    }

    private static byte[] Int(int v) => BitConverter.GetBytes(v);

    public ProbeResult Send(IPAddress destination, IPAddress? source, int ttl, int timeoutMs, byte[] payload, bool dontFragment)
    {
        if (!Equals(source, _bound))
        {
            _socket.Dispose();
            _socket = Open(source);
        }
        if (ttl != _ttl)
        {
            _socket.SetRawSocketOption(V6 ? SOL_IPV6 : SOL_IP, V6 ? IPV6_UNICAST_HOPS : IP_TTL, Int(Math.Clamp(ttl, 1, 255)));
            _ttl = ttl;
        }
        var df = dontFragment ? 1 : 0;
        if (df != _df)
        {
            _socket.SetRawSocketOption(V6 ? SOL_IPV6 : SOL_IP, V6 ? IPV6_MTU_DISCOVER : IP_MTU_DISCOVER, Int(dontFragment ? IP_PMTUDISC_DO : IP_PMTUDISC_DONT));
            _df = df;
        }

        var seq = ++_seq;
        var packet = new byte[8 + payload.Length];
        packet[0] = V6 ? (byte)128 : (byte)8;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), _ident);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), seq);
        payload.CopyTo(packet, 8);
        if (!V6) BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), Checksum(packet));

        Drain();
        var ts = IcmpProber.NowUs();
        var sw = Stopwatch.StartNew();
        try
        {
            _socket.SendTo(packet, new IPEndPoint(destination, 0));
        }
        catch (SocketException e)
        {
            var st = e.SocketErrorCode switch
            {
                SocketError.NetworkUnreachable => ProbeStatus.NetUnreachable,
                SocketError.HostUnreachable => ProbeStatus.HostUnreachable,
                SocketError.MessageSize => ProbeStatus.PacketTooBig,
                _ => ProbeStatus.Error,
            };
            return new ProbeResult(st, null, double.NaN, 0, Raw(st), ts);
        }

        while (true)
        {
            var left = timeoutMs - (int)sw.ElapsedMilliseconds;
            if (left <= 0) return Timeout(ts);
            var pfd = new PollFd { Fd = (int)_socket.Handle, Events = (short)(POLLIN | POLLERR) };
            var rc = poll(&pfd, 1, left);
            if (rc <= 0) return Timeout(ts);

            if ((pfd.REvents & POLLERR) != 0 && !_raw)
            {
                var r = ReadErrorQueue(seq, sw, ts);
                if (r != null) return r.Value;
            }
            if ((pfd.REvents & POLLIN) != 0)
            {
                var r = ReadReply(destination, seq, sw, ts);
                if (r != null) return r.Value;
            }
        }
    }

    private static ProbeResult Timeout(long ts) => new(ProbeStatus.Timeout, null, double.NaN, 0, 11010, ts);

    /// <summary>Drops late replies and errors belonging to earlier probes.</summary>
    private void Drain()
    {
        for (var i = 0; i < 64; i++)
        {
            try
            {
                if (_socket.Available <= 0) break;
                EndPoint ep = new IPEndPoint(V6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                _socket.ReceiveFrom(_rx, ref ep);
            }
            catch (SocketException)
            {
                break;
            }
        }
        if (_raw) return;
        for (var i = 0; i < 64; i++)
            if (RecvErr(out _, out _) < 0) break;
    }

    private ProbeResult? ReadReply(IPAddress destination, ushort seq, Stopwatch sw, long ts)
    {
        int n;
        EndPoint ep = new IPEndPoint(V6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        try
        {
            n = _socket.ReceiveFrom(_rx, ref ep);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        var rtt = sw.Elapsed.TotalMilliseconds;
        var from = ((IPEndPoint)ep).Address;
        var data = _rx.AsSpan(0, n);

        if (!_raw)
        {
            // Ping socket: ICMP header + data, the kernel already matched the identifier.
            if (n < 8 || data[0] != (V6 ? 129 : 0) || BinaryPrimitives.ReadUInt16BigEndian(data[6..]) != seq) return null;
            return new ProbeResult(ProbeStatus.Ok, from, rtt, (uint)Math.Round(rtt), 0, ts);
        }

        // Raw socket: every ICMP packet on the host arrives here; keep only ours.
        var icmp = data;
        if (!V6)
        {
            if (n < 20) return null;
            var ihl = (data[0] & 0x0F) * 4;
            if (n < ihl + 8) return null;
            icmp = data[ihl..];
        }
        var type = icmp[0];
        var code = icmp[1];
        if (type == (V6 ? 129 : 0))
        {
            if (BinaryPrimitives.ReadUInt16BigEndian(icmp[4..]) != _ident || BinaryPrimitives.ReadUInt16BigEndian(icmp[6..]) != seq) return null;
            return new ProbeResult(ProbeStatus.Ok, from, rtt, (uint)Math.Round(rtt), 0, ts);
        }
        if (!QuotedMatches(icmp, seq)) return null;
        var st = V6 ? MapIcmp6(type, code) : MapIcmp4(type, code);
        if (st == ProbeStatus.Error) return null;
        return new ProbeResult(st, from, rtt, (uint)Math.Round(rtt), Raw(st), ts);
    }

    /// <summary>Error messages quote the original packet: check its identifier and sequence.</summary>
    private bool QuotedMatches(ReadOnlySpan<byte> icmp, ushort seq)
    {
        if (icmp.Length < 8) return false;
        var inner = icmp[8..];
        int off;
        if (V6)
        {
            if (inner.Length < 48 || inner[6] != 58) return false; // next header must be ICMPv6
            off = 40;
        }
        else
        {
            if (inner.Length < 28 || inner[9] != 1) return false; // protocol must be ICMP
            off = (inner[0] & 0x0F) * 4;
            if (inner.Length < off + 8) return false;
        }
        return BinaryPrimitives.ReadUInt16BigEndian(inner[(off + 4)..]) == _ident
               && BinaryPrimitives.ReadUInt16BigEndian(inner[(off + 6)..]) == seq;
    }

    private ProbeResult? ReadErrorQueue(ushort seq, Stopwatch sw, long ts)
    {
        while (true)
        {
            var n = RecvErr(out var err, out var origSeq);
            if (n < 0) return null;
            if (err == null || origSeq != seq) continue;
            var rtt = sw.Elapsed.TotalMilliseconds;
            var e = err.Value;
            ProbeStatus st;
            if (e.Origin == SO_EE_ORIGIN_LOCAL)
                st = e.Errno == EMSGSIZE ? ProbeStatus.PacketTooBig : ProbeStatus.Error;
            else if (e.Origin is SO_EE_ORIGIN_ICMP or SO_EE_ORIGIN_ICMP6)
                st = e.Origin == SO_EE_ORIGIN_ICMP6 ? MapIcmp6(e.Type, e.Code) : MapIcmp4(e.Type, e.Code);
            else
                continue;
            if (e.Offender == null) return new ProbeResult(st, null, double.NaN, 0, Raw(st), ts);
            return new ProbeResult(st, e.Offender, rtt, (uint)Math.Round(rtt), Raw(st), ts);
        }
    }

    internal readonly record struct ExtErr(uint Errno, byte Origin, byte Type, byte Code, IPAddress? Offender);

    /// <summary>One recvmsg(MSG_ERRQUEUE). Returns bytes read or -1 when the queue is empty.</summary>
    private int RecvErr(out ExtErr? err, out int origSeq)
    {
        err = null;
        origSeq = -1;
        fixed (byte* rx = _rx)
        fixed (byte* ctl = _ctl)
        fixed (byte* name = _name)
        {
            var iov = new IoVec { Base = rx, Len = (nuint)_rx.Length };
            var msg = new MsgHdr
            {
                Name = name,
                NameLen = (uint)_name.Length,
                Iov = &iov,
                IovLen = 1,
                Control = ctl,
                ControlLen = (nuint)_ctl.Length,
            };
            var n = recvmsg((int)_socket.Handle, &msg, MSG_ERRQUEUE | MSG_DONTWAIT);
            if (n < 0) return -1;
            // The payload is the original ICMP message we sent: sequence at offset 6.
            if (n >= 8) origSeq = BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(rx + 6, 2));
            err = ParseControl(new ReadOnlySpan<byte>(ctl, (int)msg.ControlLen));
            return (int)n;
        }
    }

    /// <summary>Walks cmsghdr records (len: size_t, level: int, type: int, data aligned to 8).</summary>
    internal static ExtErr? ParseControl(ReadOnlySpan<byte> control)
    {
        var p = 0;
        while (p + 16 <= control.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt64LittleEndian(control[p..]);
            var level = BinaryPrimitives.ReadInt32LittleEndian(control[(p + 8)..]);
            var type = BinaryPrimitives.ReadInt32LittleEndian(control[(p + 12)..]);
            if (len < 16 || p + len > control.Length) break;
            if ((level == SOL_IP && type == IP_RECVERR) || (level == SOL_IPV6 && type == IPV6_RECVERR))
            {
                var d = control.Slice(p + 16, len - 16);
                if (d.Length < 16) break;
                // struct sock_extended_err { u32 ee_errno; u8 ee_origin, ee_type, ee_code, ee_pad; u32 ee_info, ee_data; }
                var errno = BinaryPrimitives.ReadUInt32LittleEndian(d);
                var origin = d[4];
                var t = d[5];
                var c = d[6];
                IPAddress? offender = null;
                // SO_EE_OFFENDER: the sockaddr right after the struct.
                if (d.Length >= 16 + 8)
                {
                    var sa = d[16..];
                    var fam = BinaryPrimitives.ReadUInt16LittleEndian(sa);
                    if (fam == 2 && sa.Length >= 8) offender = new IPAddress(sa.Slice(4, 4));
                    else if (fam == 10 && sa.Length >= 24) offender = new IPAddress(sa.Slice(8, 16).ToArray());
                    if (offender != null && (offender.Equals(IPAddress.Any) || offender.Equals(IPAddress.IPv6Any))) offender = null;
                }
                return new ExtErr(errno, origin, t, c, offender);
            }
            p += (len + 7) & ~7;
        }
        return null;
    }

    internal static ProbeStatus MapIcmp4(byte type, byte code) => type switch
    {
        11 => ProbeStatus.TtlExpired,
        3 => code switch
        {
            0 or 6 => ProbeStatus.NetUnreachable,
            1 or 7 => ProbeStatus.HostUnreachable,
            2 => ProbeStatus.ProtocolUnreachable,
            3 => ProbeStatus.PortUnreachable,
            4 => ProbeStatus.PacketTooBig,
            9 or 10 or 13 => ProbeStatus.Prohibited,
            _ => ProbeStatus.HostUnreachable,
        },
        12 => ProbeStatus.ParamProblem,
        4 => ProbeStatus.SourceQuench,
        _ => ProbeStatus.Error,
    };

    internal static ProbeStatus MapIcmp6(byte type, byte code) => type switch
    {
        3 => ProbeStatus.TtlExpired,
        1 => code switch
        {
            0 => ProbeStatus.NetUnreachable,
            1 or 5 or 6 => ProbeStatus.Prohibited,
            4 => ProbeStatus.PortUnreachable,
            _ => ProbeStatus.HostUnreachable,
        },
        2 => ProbeStatus.PacketTooBig,
        4 => ProbeStatus.ParamProblem,
        _ => ProbeStatus.Error,
    };

    /// <summary>Same numeric vocabulary as Windows IP_STATUS so logs and exports look identical.</summary>
    internal static uint Raw(ProbeStatus s) => s switch
    {
        ProbeStatus.Ok => 0,
        ProbeStatus.NetUnreachable => 11002,
        ProbeStatus.HostUnreachable => 11003,
        ProbeStatus.ProtocolUnreachable => 11004,
        ProbeStatus.PortUnreachable => 11005,
        ProbeStatus.PacketTooBig => 11009,
        ProbeStatus.Timeout => 11010,
        ProbeStatus.TtlExpired => 11013,
        ProbeStatus.ParamProblem => 11015,
        ProbeStatus.SourceQuench => 11016,
        ProbeStatus.Prohibited => 11044,
        _ => 11050,
    };

    internal static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var i = 0;
        for (; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        if (i < data.Length) sum += (uint)(data[i] << 8);
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    public void Dispose() => _socket.Dispose();
}
