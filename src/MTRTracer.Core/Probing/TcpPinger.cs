using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MTRTracer.Core.Probing;

public readonly record struct TcpPingResult(bool Reachable, bool Refused, double RttMs, string Status);

/// <summary>
/// Measures TCP handshake time to the target. Used when the destination drops ICMP echo.
/// A refused connection (RST) still proves reachability and yields a valid RTT.
/// </summary>
public static class TcpPinger
{
    public static async Task<TcpPingResult> PingAsync(IPAddress target, int port, IPAddress? source, int timeoutMs, CancellationToken ct)
    {
        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        socket.LingerState = new LingerOption(true, 0);
        if (source != null)
        {
            try { socket.Bind(new IPEndPoint(source, 0)); }
            catch (SocketException e) { return new(false, false, double.NaN, "bind-" + e.SocketErrorCode); }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(target, port), cts.Token);
            var rtt = sw.Elapsed.TotalMilliseconds;
            return new(true, false, rtt, "open");
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return new(true, true, sw.Elapsed.TotalMilliseconds, "refused");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, false, double.NaN, "timeout");
        }
        catch (SocketException e)
        {
            return new(false, false, double.NaN, e.SocketErrorCode.ToString());
        }
    }
}

/// <summary>Path MTU discovery: binary search of the largest unfragmented ICMP echo that reaches the target.</summary>
public static class PmtuProbe
{
    public static int? Discover(IPAddress target, IPAddress? source, int timeoutMs, CancellationToken ct)
    {
        var v6 = target.AddressFamily == AddressFamily.InterNetworkV6;
        var header = v6 ? 48 : 28; // IP header + ICMP header
        int lo = v6 ? 1280 : 576, hi = 9000, best = 0;
        using var prober = IcmpProber.Create(target.AddressFamily, hi);
        bool Works(int mtu)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (ct.IsCancellationRequested) return false;
                var payload = IcmpProber.BuildPayload(mtu - header, "PMTU");
                var r = prober.Send(target, source, 128, timeoutMs, payload, dontFragment: true);
                if (r.Status == ProbeStatus.Ok) return true;
                if (r.Status == ProbeStatus.PacketTooBig) return false;
            }
            return false;
        }

        if (!Works(lo)) return null;
        best = lo;
        while (lo <= hi && !ct.IsCancellationRequested)
        {
            var mid = (lo + hi) / 2;
            if (Works(mid)) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }
}
