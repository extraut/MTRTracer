using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MTRTracer.Core.Dns;

public sealed record ResolveAttempt(string Resolver, string Via, double Ms, bool Ok, string? Error, List<string> Addresses);

public sealed record ResolveResult(
    string Input,
    string Host,
    IPAddress? Address,
    List<string> Addresses,
    string Via,
    double Ms,
    List<ResolveAttempt> Attempts);

public sealed record AsnInfo(int Asn, string? Prefix, string? Country, string? Registry, string? Allocated, string? Name);

/// <summary>
/// Name resolution with a fallback chain: system resolver → adapter DNS servers → public resolvers.
/// Also reverse DNS for hops and ASN lookups via Team Cymru's DNS interface.
/// </summary>
public sealed class ResolverService
{
    public static readonly IPAddress[] PublicResolvers =
    [
        IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("9.9.9.9"),
    ];

    private readonly ConcurrentDictionary<string, Task<string?>> _ptrCache = new();
    private readonly ConcurrentDictionary<string, Task<AsnInfo?>> _asnCache = new();
    private readonly ConcurrentDictionary<int, Task<string?>> _asNameCache = new();

    public IReadOnlyList<IPAddress> AdapterDns { get; set; } = [];

    private IEnumerable<IPAddress> Resolvers() =>
        AdapterDns.Where(a => a.AddressFamily == AddressFamily.InterNetwork || !a.IsIPv6SiteLocal)
            .Concat(PublicResolvers).Distinct();

    public async Task<ResolveResult> ResolveTargetAsync(string input, string family, CancellationToken ct)
    {
        var host = input;
        var attempts = new List<ResolveAttempt>();
        if (IPAddress.TryParse(host, out var literal))
        {
            var s = AdaptersFormat(literal);
            return new ResolveResult(input, host, literal, [s], "literal", 0, attempts);
        }

        bool Wanted(IPAddress a) => family switch
        {
            "4" => a.AddressFamily == AddressFamily.InterNetwork,
            "6" => a.AddressFamily == AddressFamily.InterNetworkV6,
            _ => true,
        };

        IPAddress? Pick(IEnumerable<IPAddress> list)
        {
            var l = list.Where(Wanted).ToList();
            if (family == "auto")
                return l.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? l.FirstOrDefault();
            return l.FirstOrDefault();
        }

        // 1. System resolver (respects hosts file, VPN split DNS, NRPT policies).
        var sw = Stopwatch.StartNew();
        try
        {
            var addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct).WaitAsync(TimeSpan.FromSeconds(4), ct);
            var ms = sw.Elapsed.TotalMilliseconds;
            var list = addrs.Select(AdaptersFormat).Distinct().ToList();
            attempts.Add(new ResolveAttempt("system", "system", ms, addrs.Length > 0, null, list));
            var chosen = Pick(addrs);
            if (chosen != null)
                return new ResolveResult(input, host, chosen, list, "system", ms, attempts);
        }
        catch (Exception e) when (e is SocketException or TimeoutException)
        {
            attempts.Add(new ResolveAttempt("system", "system", sw.Elapsed.TotalMilliseconds, false, Describe(e), []));
        }

        // 2. Direct queries to the adapter's DNS servers, then public resolvers.
        var types = family switch
        {
            "6" => new[] { DnsType.AAAA },
            "4" => new[] { DnsType.A },
            _ => new[] { DnsType.A, DnsType.AAAA },
        };
        foreach (var server in Resolvers())
        {
            var via = PublicResolvers.Contains(server) ? "public-dns" : "adapter-dns";
            foreach (var type in types)
            {
                try
                {
                    var r = await MiniDnsClient.QueryAsync(server, host, type, 2500, ct);
                    var list = r.Addresses.Select(AdaptersFormat).ToList();
                    attempts.Add(new ResolveAttempt(server.ToString(), via, r.ElapsedMs, r.Ok && list.Count > 0,
                        r.Ok ? (list.Count == 0 ? Loc.S($"нет записей {type}", $"no {type} records") : null) : RCodeText(r.RCode), list));
                    var chosen = Pick(r.Addresses);
                    if (chosen != null)
                        return new ResolveResult(input, host, chosen, list, via, r.ElapsedMs, attempts);
                    if (r.RCode == 3) break; // NXDOMAIN: other record types will not help
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    attempts.Add(new ResolveAttempt(server.ToString(), via, 2500, false, Describe(e), []));
                }
            }
        }
        return new ResolveResult(input, host, null, [], "none", 0, attempts);
    }

    /// <summary>Timing of each resolver for the details panel. Runs in parallel, never throws.</summary>
    public async Task<List<ResolveAttempt>> BenchmarkAsync(string host, CancellationToken ct)
    {
        var tasks = Resolvers().Select(async server =>
        {
            var via = PublicResolvers.Contains(server) ? "public-dns" : "adapter-dns";
            try
            {
                var r = await MiniDnsClient.QueryAsync(server, host, DnsType.A, 2500, ct);
                var list = r.Addresses.Select(AdaptersFormat).ToList();
                return new ResolveAttempt(server.ToString(), via, r.ElapsedMs, r.Ok, r.Ok ? null : RCodeText(r.RCode), list);
            }
            catch (Exception e)
            {
                return new ResolveAttempt(server.ToString(), via, 2500, false, Describe(e), []);
            }
        });
        return [.. await Task.WhenAll(tasks)];
    }

    public Task<string?> ReverseAsync(IPAddress ip) =>
        _ptrCache.GetOrAdd(ip.ToString(), _ => ReverseCoreAsync(ip));

    private async Task<string?> ReverseCoreAsync(IPAddress ip)
    {
        try
        {
            var entry = await System.Net.Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromSeconds(2.5));
            if (!string.IsNullOrEmpty(entry.HostName) && entry.HostName != ip.ToString())
                return entry.HostName.TrimEnd('.');
        }
        catch { }

        foreach (var server in Resolvers().Take(3))
        {
            try
            {
                var r = await MiniDnsClient.QueryAsync(server, MiniDnsClient.ReverseName(ip), DnsType.PTR, 2000, CancellationToken.None);
                var ptr = r.Answers.FirstOrDefault(a => a.Type == DnsType.PTR)?.Data;
                if (ptr != null) return ptr.TrimEnd('.');
                if (r.Ok || r.RCode == 3) return null;
            }
            catch { }
        }
        return null;
    }

    public Task<AsnInfo?> AsnAsync(IPAddress ip) =>
        _asnCache.GetOrAdd(ip.ToString(), _ => AsnCoreAsync(ip));

    private async Task<AsnInfo?> AsnCoreAsync(IPAddress ip)
    {
        var name = ip.AddressFamily == AddressFamily.InterNetwork
            ? string.Join('.', ip.GetAddressBytes().Reverse()) + ".origin.asn.cymru.com"
            : string.Join('.', MiniDnsClient.NibblesReversed(ip)) + ".origin6.asn.cymru.com";
        var txt = await TxtAsync(name);
        if (txt == null) return null;
        // "13335 | 1.1.1.0/24 | AU | apnic | 2011-08-11"
        var parts = txt.Split('|').Select(p => p.Trim()).ToArray();
        if (parts.Length < 1 || !int.TryParse(parts[0].Split(' ')[0], out var asn)) return null;
        var asName = await _asNameCache.GetOrAdd(asn, async n =>
        {
            var t = await TxtAsync($"AS{n}.asn.cymru.com");
            // "13335 | US | arin | 2010-07-14 | CLOUDFLARENET, US"
            var p = t?.Split('|');
            return p is { Length: >= 5 } ? p[4].Trim() : null;
        });
        return new AsnInfo(asn, At(parts, 1), At(parts, 2), At(parts, 3), At(parts, 4), asName);
    }

    private async Task<string?> TxtAsync(string name)
    {
        foreach (var server in Resolvers().Take(3))
        {
            try
            {
                var r = await MiniDnsClient.QueryAsync(server, name, DnsType.TXT, 2500, CancellationToken.None);
                var txt = r.Answers.FirstOrDefault(a => a.Type == DnsType.TXT)?.Data;
                if (txt != null) return txt;
                if (r.Ok || r.RCode == 3) return null;
            }
            catch { }
        }
        return null;
    }

    private static string? At(string[] a, int i) => i < a.Length && a[i].Length > 0 ? a[i] : null;

    private static string AdaptersFormat(IPAddress a) => Adapters.AdapterService.Format(a);

    public static string RCodeText(int rcode) => rcode switch
    {
        1 => "FORMERR",
        2 => "SERVFAIL",
        3 => Loc.S("NXDOMAIN: домен не существует", "NXDOMAIN: the domain does not exist"),
        4 => "NOTIMP",
        5 => "REFUSED",
        _ => $"RCODE {rcode}",
    };

    private static string Describe(Exception e) => e switch
    {
        SocketException se => se.SocketErrorCode switch
        {
            SocketError.HostNotFound => Loc.S("домен не найден", "domain not found"),
            SocketError.TryAgain => Loc.S("временная ошибка DNS", "temporary DNS failure"),
            SocketError.NoData => Loc.S("нет адресов", "no addresses"),
            _ => se.SocketErrorCode.ToString(),
        },
        TimeoutException or OperationCanceledException => Loc.S("таймаут", "timeout"),
        _ => e.Message,
    };
}
