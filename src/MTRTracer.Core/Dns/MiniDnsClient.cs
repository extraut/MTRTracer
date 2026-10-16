using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MTRTracer.Core.Dns;

public enum DnsType : ushort
{
    A = 1,
    CNAME = 5,
    PTR = 12,
    TXT = 16,
    AAAA = 28,
}

public sealed record DnsRecord(string Name, DnsType Type, uint Ttl, string Data);

public sealed record DnsResponse(int RCode, IReadOnlyList<DnsRecord> Answers, double ElapsedMs, string Server)
{
    public bool Ok => RCode == 0;
    public IEnumerable<IPAddress> Addresses => Answers
        .Where(a => a.Type is DnsType.A or DnsType.AAAA)
        .Select(a => IPAddress.Parse(a.Data));
}

/// <summary>
/// Small DNS-over-UDP client (RFC 1035). Lets us query a specific resolver (the adapter's DNS server,
/// or a public one as fallback), measure its latency, and issue TXT/PTR queries .NET does not expose.
/// </summary>
public static class MiniDnsClient
{
    private static int _nextId = Random.Shared.Next(ushort.MaxValue);

    public static async Task<DnsResponse> QueryAsync(IPAddress server, string name, DnsType type, int timeoutMs, CancellationToken ct, IPAddress? source = null)
    {
        var id = (ushort)Interlocked.Increment(ref _nextId);
        var query = BuildQuery(id, name, type);
        using var udp = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (source != null && source.AddressFamily == server.AddressFamily)
            udp.Bind(new IPEndPoint(source, 0));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var sw = Stopwatch.StartNew();
        await udp.SendToAsync(query, new IPEndPoint(server, 53), cts.Token);
        var buf = new byte[4096];
        while (true)
        {
            var res = await udp.ReceiveFromAsync(buf, new IPEndPoint(server.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0), cts.Token);
            var span = buf.AsSpan(0, res.ReceivedBytes);
            if (span.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(span) != id) continue;
            var elapsed = sw.Elapsed.TotalMilliseconds;
            var (rcode, answers) = Parse(span);
            return new DnsResponse(rcode, answers, elapsed, server.ToString());
        }
    }

    internal static byte[] BuildQuery(ushort id, string name, DnsType type)
    {
        var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // RD
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);      // QDCOUNT
        ms.Write(header);
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(IdnLabel(label));
            if (bytes.Length > 63) throw new ArgumentException("DNS label too long");
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail, (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1); // IN
        ms.Write(tail);
        return ms.ToArray();
    }

    private static string IdnLabel(string label) =>
        label.All(c => c < 128) ? label : new System.Globalization.IdnMapping().GetAscii(label);

    internal static (int rcode, List<DnsRecord> answers) Parse(ReadOnlySpan<byte> msg)
    {
        var flags = BinaryPrimitives.ReadUInt16BigEndian(msg[2..]);
        var rcode = flags & 0xF;
        var qd = BinaryPrimitives.ReadUInt16BigEndian(msg[4..]);
        var an = BinaryPrimitives.ReadUInt16BigEndian(msg[6..]);
        var pos = 12;
        for (var i = 0; i < qd; i++)
        {
            ReadName(msg, ref pos);
            pos += 4;
        }
        var answers = new List<DnsRecord>();
        for (var i = 0; i < an && pos < msg.Length; i++)
        {
            var name = ReadName(msg, ref pos);
            var type = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(msg[pos..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(msg[(pos + 4)..]);
            var rdlen = BinaryPrimitives.ReadUInt16BigEndian(msg[(pos + 8)..]);
            pos += 10;
            var rdStart = pos;
            string? data = type switch
            {
                DnsType.A when rdlen == 4 => new IPAddress(msg.Slice(pos, 4)).ToString(),
                DnsType.AAAA when rdlen == 16 => new IPAddress(msg.Slice(pos, 16)).ToString(),
                DnsType.CNAME or DnsType.PTR => ReadName(msg, ref pos),
                DnsType.TXT => ReadTxt(msg.Slice(pos, rdlen)),
                _ => null,
            };
            pos = rdStart + rdlen;
            if (data != null) answers.Add(new DnsRecord(name, type, ttl, data));
        }
        return (rcode, answers);
    }

    private static string ReadTxt(ReadOnlySpan<byte> rd)
    {
        var sb = new StringBuilder();
        var p = 0;
        while (p < rd.Length)
        {
            var len = rd[p++];
            sb.Append(Encoding.UTF8.GetString(rd.Slice(p, Math.Min(len, rd.Length - p))));
            p += len;
        }
        return sb.ToString();
    }

    private static string ReadName(ReadOnlySpan<byte> msg, ref int pos)
    {
        var sb = new StringBuilder();
        var p = pos;
        var jumped = false;
        var hops = 0;
        while (p < msg.Length)
        {
            var len = msg[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                var ptr = BinaryPrimitives.ReadUInt16BigEndian(msg[p..]) & 0x3FFF;
                if (!jumped) pos = p + 2;
                jumped = true;
                p = ptr;
                if (++hops > 32) break;
                continue;
            }
            p++;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(msg.Slice(p, Math.Min(len, msg.Length - p))));
            p += len;
        }
        if (!jumped) pos = p;
        return sb.ToString();
    }

    public static string ReverseName(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
        }
        return string.Join('.', NibblesReversed(ip)) + ".ip6.arpa";
    }

    public static IEnumerable<string> NibblesReversed(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            yield return (bytes[i] & 0xF).ToString("x");
            yield return (bytes[i] >> 4).ToString("x");
        }
    }
}
