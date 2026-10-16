using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MTRTracer.Core.Native;

namespace MTRTracer.Core.Probing;

public enum ProbeStatus
{
    Ok,
    TtlExpired,
    Timeout,
    NetUnreachable,
    HostUnreachable,
    ProtocolUnreachable,
    PortUnreachable,
    Prohibited,
    PacketTooBig,
    ParamProblem,
    SourceQuench,
    BadRoute,
    NoResources,
    Error,
}

public readonly record struct ProbeResult(
    ProbeStatus Status,
    IPAddress? From,
    double RttMs,
    uint KernelRttMs,
    uint RawStatus,
    long TimestampUs);

/// <summary>One ICMP echo sender, used from a single thread.</summary>
public interface IIcmpProber : IDisposable
{
    ProbeResult Send(IPAddress destination, IPAddress? source, int ttl, int timeoutMs, byte[] payload, bool dontFragment);
}

/// <summary>Platform selection and helpers shared by the ICMP probers.</summary>
public static class IcmpProber
{
    public static IIcmpProber Create(AddressFamily family, int maxPayload = 65500) =>
        OperatingSystem.IsWindows()
            ? new WindowsIcmpProber(family, maxPayload)
            : LinuxIcmpProber.Create(family, maxPayload);

    public static byte[] BuildPayload(int size, string pattern)
    {
        var data = new byte[Math.Max(0, size)];
        var p = string.IsNullOrEmpty(pattern) ? "MTRTracer" : pattern;
        for (var i = 0; i < data.Length; i++) data[i] = (byte)p[i % p.Length];
        return data;
    }

    /// <summary>Windows IP_STATUS codes (also used as the common status vocabulary on Linux).</summary>
    public static ProbeStatus MapStatus(uint s) => s switch
    {
        0 => ProbeStatus.Ok,
        11002 => ProbeStatus.NetUnreachable,
        11003 => ProbeStatus.HostUnreachable,
        11004 => ProbeStatus.ProtocolUnreachable, // IPv6: IP_DEST_PROHIBITED shares this value
        11005 => ProbeStatus.PortUnreachable,
        11006 => ProbeStatus.NoResources,
        11009 => ProbeStatus.PacketTooBig,
        11010 => ProbeStatus.Timeout,
        11012 => ProbeStatus.BadRoute,
        11013 or 11014 or 11041 => ProbeStatus.TtlExpired,
        11015 or 11042 or 11043 => ProbeStatus.ParamProblem,
        11016 => ProbeStatus.SourceQuench,
        11040 => ProbeStatus.HostUnreachable,
        11044 or 11045 => ProbeStatus.Prohibited,
        1231 => ProbeStatus.NetUnreachable,   // ERROR_NETWORK_UNREACHABLE
        1232 => ProbeStatus.HostUnreachable,  // ERROR_HOST_UNREACHABLE
        _ => ProbeStatus.Error,
    };

    public static string StatusCode(ProbeStatus s) => s switch
    {
        ProbeStatus.Ok => "ok",
        ProbeStatus.TtlExpired => "ttl-expired",
        ProbeStatus.Timeout => "timeout",
        ProbeStatus.NetUnreachable => "net-unreachable",
        ProbeStatus.HostUnreachable => "host-unreachable",
        ProbeStatus.ProtocolUnreachable => "proto-unreachable",
        ProbeStatus.PortUnreachable => "port-unreachable",
        ProbeStatus.Prohibited => "prohibited",
        ProbeStatus.PacketTooBig => "packet-too-big",
        ProbeStatus.ParamProblem => "param-problem",
        ProbeStatus.SourceQuench => "source-quench",
        ProbeStatus.BadRoute => "bad-route",
        ProbeStatus.NoResources => "no-resources",
        _ => "error",
    };

    private static readonly long StartTicks = Stopwatch.GetTimestamp();
    private static readonly long StartUnixUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    /// <summary>Monotonic wall clock in microseconds since Unix epoch.</summary>
    public static long NowUs() =>
        StartUnixUs + (long)((Stopwatch.GetTimestamp() - StartTicks) * 1_000_000.0 / Stopwatch.Frequency);
}

/// <summary>
/// Windows: IcmpSendEcho2Ex / Icmp6SendEcho2 in synchronous mode, which lets us set TTL, DF and the
/// source address (adapter binding) without admin rights. RTT is measured with a high resolution
/// Stopwatch; the kernel's millisecond RTT is kept alongside.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsIcmpProber : IIcmpProber
{
    private readonly IpHlpApi.SafeIcmpHandle _handle;
    private readonly AddressFamily _family;
    private readonly IntPtr _reply;
    private readonly int _replySize;
    private byte[] _payload = [];

    public WindowsIcmpProber(AddressFamily family, int maxPayload = 65500)
    {
        _family = family;
        _handle = family == AddressFamily.InterNetworkV6 ? IpHlpApi.Icmp6CreateFile() : IpHlpApi.IcmpCreateFile();
        if (_handle.IsInvalid)
            throw new InvalidOperationException($"IcmpCreateFile failed: {Marshal.GetLastPInvokeError()}");
        // Reply buffer: reply struct + echoed payload + ICMP error header + slack.
        _replySize = 64 + maxPayload + 8 + 1024;
        _reply = Marshal.AllocHGlobal(_replySize);
    }

    public ProbeResult Send(IPAddress destination, IPAddress? source, int ttl, int timeoutMs, byte[] payload, bool dontFragment)
    {
        _payload = payload;
        var options = new IpHlpApi.IP_OPTION_INFORMATION
        {
            Ttl = (byte)Math.Clamp(ttl, 1, 255),
            Flags = dontFragment ? IpHlpApi.IP_FLAG_DF : (byte)0,
        };

        // Clear the reply header so a failed call never yields stale data.
        unsafe { new Span<byte>((void*)_reply, 64).Clear(); }

        var ts = IcmpProber.NowUs();
        var sw = Stopwatch.StartNew();
        uint count;
        if (_family == AddressFamily.InterNetworkV6)
        {
            var src = IpHlpApi.ToSockAddr(source, AddressFamily.InterNetworkV6);
            var dst = IpHlpApi.ToSockAddr(destination, AddressFamily.InterNetworkV6);
            count = IpHlpApi.Icmp6SendEcho2(_handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, src, dst,
                _payload, (ushort)_payload.Length, ref options, _reply, (uint)_replySize, (uint)timeoutMs);
        }
        else
        {
            var src = source == null ? 0u : IpHlpApi.ToInAddr(source);
            count = IpHlpApi.IcmpSendEcho2Ex(_handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, src,
                IpHlpApi.ToInAddr(destination), _payload, (ushort)_payload.Length, ref options,
                _reply, (uint)_replySize, (uint)timeoutMs);
        }
        var elapsed = sw.Elapsed.TotalMilliseconds;
        var lastError = count == 0 ? (uint)Marshal.GetLastPInvokeError() : 0u;

        var (from, rawStatus, kernelRtt) = ReadReply();
        if (count == 0 && rawStatus == 0)
        {
            rawStatus = lastError;
            from = null;
        }

        var status = IcmpProber.MapStatus(rawStatus);
        if (status == ProbeStatus.Timeout || from == null)
            return new ProbeResult(status == ProbeStatus.Ok ? ProbeStatus.Timeout : status, null, double.NaN, 0, rawStatus, ts);
        return new ProbeResult(status, from, elapsed, kernelRtt, rawStatus, ts);
    }

    private unsafe (IPAddress? from, uint status, uint rtt) ReadReply()
    {
        var span = new ReadOnlySpan<byte>((void*)_reply, 64);
        if (_family == AddressFamily.InterNetworkV6)
        {
            // ICMPV6_ECHO_REPLY: IPV6_ADDRESS_EX (packed: port 2, flowinfo 4, addr 16, scope 4) + Status + RTT
            var addrBytes = span.Slice(6, 16);
            var scope = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(22, 4));
            var status = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(26, 4));
            var rtt = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(30, 4));
            var allZero = addrBytes.IndexOfAnyExcept((byte)0) < 0;
            return (allZero ? null : new IPAddress(addrBytes.ToArray(), scope), status, rtt);
        }
        else
        {
            // ICMP_ECHO_REPLY: Address, Status, RoundTripTime, ...
            var addr = span[..4];
            var status = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
            var rtt = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
            var allZero = addr.IndexOfAnyExcept((byte)0) < 0;
            return (allZero ? null : new IPAddress(addr), status, rtt);
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
        Marshal.FreeHGlobal(_reply);
    }
}
