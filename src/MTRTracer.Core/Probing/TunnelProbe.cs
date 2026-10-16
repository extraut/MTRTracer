using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace MTRTracer.Core.Probing;

public readonly record struct TunnelSample(bool Ok, double RttMs, double? SetupMs, double? ConnectMs, bool ConnectFaked, string Status);

/// <summary>
/// End-to-end latency through a proxy tunnel, where ICMP and TCP handshakes are answered locally.
/// Keeps one keep-alive HTTP(S) connection to the target and times a tiny HEAD request: from the moment
/// the request leaves until the first response byte arrives. That trip really crosses the tunnel,
/// the VPN server and the internet to the target and back.
/// </summary>
public sealed class TunnelProbe : IAsyncDisposable
{
    private readonly string _host;
    private readonly IPAddress _ip;
    private readonly int _port;
    private readonly IPAddress? _source;
    private readonly int _timeoutMs;
    private Socket? _socket;
    private Stream? _stream;
    private readonly byte[] _buffer = new byte[16 * 1024];

    public TunnelProbe(string host, IPAddress ip, int port, IPAddress? source, int timeoutMs)
    {
        _host = host;
        _ip = ip;
        _port = port;
        _source = source;
        _timeoutMs = Math.Max(1500, timeoutMs);
    }

    public bool UsesTls => _port == 443;

    public async Task<TunnelSample> ProbeAsync(CancellationToken ct)
    {
        double? connectMs = null, setupMs = null;
        var faked = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeoutMs * 2);
        try
        {
            if (_stream == null)
            {
                var sw = Stopwatch.StartNew();
                _socket = new Socket(_ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                if (_source != null && _source.AddressFamily == _ip.AddressFamily) _socket.Bind(new IPEndPoint(_source, 0));
                await _socket.ConnectAsync(new IPEndPoint(_ip, _port), cts.Token);
                connectMs = sw.Elapsed.TotalMilliseconds;
                faked = connectMs < 1.0;
                Stream s = new NetworkStream(_socket, true);
                if (UsesTls)
                {
                    sw.Restart();
                    var ssl = new SslStream(s, false);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = IPAddress.TryParse(_host, out _) ? null : _host,
                        RemoteCertificateValidationCallback = (_, _, _, _) => true,
                        ApplicationProtocols = [SslApplicationProtocol.Http11],
                    }, cts.Token);
                    setupMs = sw.Elapsed.TotalMilliseconds;
                    s = ssl;
                }
                _stream = s;
            }

            var request = Encoding.ASCII.GetBytes($"HEAD / HTTP/1.1\r\nHost: {_host}\r\nUser-Agent: MTRTracer\r\nAccept: */*\r\nConnection: keep-alive\r\n\r\n");
            var timer = Stopwatch.StartNew();
            await _stream.WriteAsync(request, cts.Token);
            await _stream.FlushAsync(cts.Token);
            var n = await _stream.ReadAsync(_buffer, cts.Token);
            var rtt = timer.Elapsed.TotalMilliseconds;
            if (n == 0) throw new IOException("соединение закрыто");
            var close = await DrainHeadersAsync(n, cts.Token);
            if (close) await ResetAsync();
            return new TunnelSample(true, rtt, setupMs, connectMs, faked, UsesTls ? "https" : "http");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await ResetAsync();
            return new TunnelSample(false, double.NaN, setupMs, connectMs, faked, "таймаут");
        }
        catch (Exception e)
        {
            await ResetAsync();
            return new TunnelSample(false, double.NaN, setupMs, connectMs, faked, Describe(e));
        }
    }

    /// <summary>Reads the rest of the response headers (HEAD has no body). Returns true if the server closes.</summary>
    private async Task<bool> DrainHeadersAsync(int already, CancellationToken ct)
    {
        var text = new StringBuilder(Encoding.ASCII.GetString(_buffer, 0, already));
        while (!text.ToString().Contains("\r\n\r\n"))
        {
            var n = await _stream!.ReadAsync(_buffer, ct);
            if (n == 0) return true;
            text.Append(Encoding.ASCII.GetString(_buffer, 0, n));
            if (text.Length > 64 * 1024) return true;
        }
        var headers = text.ToString();
        return headers.Contains("Connection: close", StringComparison.OrdinalIgnoreCase)
               || headers.StartsWith("HTTP/1.0", StringComparison.Ordinal);
    }

    private static string Describe(Exception e) => e switch
    {
        SocketException se => se.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "порт закрыт",
            SocketError.TimedOut => "таймаут",
            SocketError.HostUnreachable or SocketError.NetworkUnreachable => "нет маршрута",
            _ => se.SocketErrorCode.ToString(),
        },
        System.Security.Authentication.AuthenticationException => "ошибка TLS",
        IOException => "соединение оборвано",
        _ => e.Message,
    };

    private Task ResetAsync()
    {
        try { _stream?.Dispose(); } catch { }
        try { _socket?.Dispose(); } catch { }
        _stream = null;
        _socket = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await ResetAsync();
}
