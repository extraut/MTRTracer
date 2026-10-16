using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using MTRTracer.Core.Adapters;
using MTRTracer.Core.Geo;
using MTRTracer.Core.Native;
using MTRTracer.Core.Protocol;

namespace MTRTracer.Core.Connections;

public sealed record ConnectionDto(
    string Key,
    string Proto,
    int Family,
    string LocalAddr,
    int LocalPort,
    string? RemoteAddr,
    int? RemotePort,
    string State,
    int Pid,
    string Process,
    string? Path,
    string? AdapterId,
    string? AdapterName,
    string RemoteKind,
    string? RemoteHost,
    string? RemoteCountry,
    string? RemoteCity,
    string? RemoteAsName,
    long FirstSeen);

public sealed record RawConnection(string Proto, IPAddress Local, int LocalPort, IPAddress? Remote, int? RemotePort, string State, int Pid);

/// <summary>
/// Polls the TCP/UDP tables (GetExtendedTcpTable / GetExtendedUdpTable, no admin needed) once per second,
/// maps sockets to processes and adapters, and reports diffs as open/close events.
/// </summary>
public sealed class ConnectionMonitor : IDisposable
{
    private readonly IEventSink _sink;
    private readonly HostInfoService _hosts;
    private readonly ConcurrentDictionary<int, (string Name, string? Path)> _procCache = new();
    private Dictionary<string, ConnectionDto> _current = new();
    private Timer? _timer;
    private int _subscribers;
    private int _busy;
    private Dictionary<string, (string Id, string Name)> _localToAdapter = new();
    private long _adapterMapAt;

    public ConnectionMonitor(IEventSink sink, HostInfoService hosts)
    {
        _sink = sink;
        _hosts = hosts;
    }

    public void Subscribe(bool on)
    {
        if (on)
        {
            if (Interlocked.Increment(ref _subscribers) == 1)
                _timer = new Timer(_ => Tick(), null, 0, 1000);
        }
        else if (Interlocked.Decrement(ref _subscribers) <= 0)
        {
            _subscribers = 0;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            RefreshAdapterMap();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var next = new Dictionary<string, ConnectionDto>();
            foreach (var c in ReadAll().Select(r => Make(r.Proto, r.Local.AddressFamily, r.Local, r.LocalPort, r.Remote, r.RemotePort, r.State, r.Pid)))
            {
                var first = _current.TryGetValue(c.Key, out var prev) ? prev.FirstSeen : now;
                next[c.Key] = c with { FirstSeen = first };
            }

            var opened = next.Values.Where(c => !_current.ContainsKey(c.Key)).ToList();
            var closed = _current.Values.Where(c => !next.ContainsKey(c.Key)).ToList();
            var isFirst = _current.Count == 0;
            _current = next;
            _sink.Emit("connections.snapshot", new { t = now, items = next.Values.ToList() });
            if (!isFirst && (opened.Count > 0 || closed.Count > 0))
                _sink.Emit("connections.events", new
                {
                    t = now,
                    opened = opened.Where(c => c.RemoteAddr != null).Select(Brief),
                    closed = closed.Where(c => c.RemoteAddr != null).Select(Brief),
                });
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static object Brief(ConnectionDto c) => new { c.Key, c.Proto, c.Process, c.Pid, c.RemoteAddr, c.RemotePort, c.RemoteHost, c.LocalPort };

    private void RefreshAdapterMap()
    {
        if (Environment.TickCount64 - _adapterMapAt < 10_000) return;
        _adapterMapAt = Environment.TickCount64;
        var map = new Dictionary<string, (string, string)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    map[AdapterService.Format(u.Address)] = (ni.Id, ni.Name);
            }
            catch { }
        }
        _localToAdapter = map;
    }

    private ConnectionDto Make(string proto, AddressFamily fam, IPAddress local, int lport, IPAddress? remote, int? rport, string state, int pid)
    {
        var (name, path) = Process(pid);
        var l = AdapterService.Format(local);
        var adapter = _localToAdapter.TryGetValue(l, out var a) ? a : default;
        string kind = "none";
        string? host = null, country = null, city = null, asName = null;
        if (remote != null)
        {
            var info = _hosts.Quick(remote);
            kind = info.Kind;
            host = info.Hostname;
            var g = info.Best();
            country = g?.CountryCode;
            city = g?.City;
            asName = info.AsName ?? info.Isp;
        }
        var r = remote == null ? null : AdapterService.Format(remote);
        var key = $"{proto}|{l}|{lport}|{r}|{rport}|{pid}";
        return new ConnectionDto(key, proto, fam == AddressFamily.InterNetworkV6 ? 6 : 4, l, lport, r, rport, state, pid, name, path,
            adapter.Id, adapter.Name, kind, host, country, city, asName, 0);
    }

    public static (string Name, string? Path) ProcessInfo(int pid) => Shared.Process(pid);
    private static readonly ConnectionMonitor Shared = new(new NullSink(), null!);

    private (string, string?) Process(int pid)
    {
        if (!OperatingSystem.IsWindows()) return LinuxSystem.ProcessInfo(pid);
        if (pid == 0) return ("System Idle", null);
        if (pid == 4) return ("System", null);
        if (_procCache.TryGetValue(pid, out var cached)) return cached;
        string name = $"PID {pid}";
        string? path = null;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            name = p.ProcessName;
        }
        catch { }
        path = QueryPath(pid);
        var v = (name, path);
        _procCache[pid] = v;
        if (_procCache.Count > 4096) _procCache.Clear();
        return v;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private static string? QueryPath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static readonly string[] TcpStates =
        ["?", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED", "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_WAIT", "DELETE_TCB"];

    private static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private delegate uint TableFn(IntPtr table, ref int size, bool order, int af, int cls, uint reserved);

    private static byte[]? ReadTable(TableFn fn, int af, int cls)
    {
        var size = 0;
        fn(IntPtr.Zero, ref size, false, af, cls, 0);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            size += 4096;
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                var rc = fn(ptr, ref size, false, af, cls, 0);
                if (rc == 0)
                {
                    var data = new byte[size];
                    Marshal.Copy(ptr, data, 0, size);
                    return data;
                }
                if (rc != IpHlpApi.ERROR_INSUFFICIENT_BUFFER) return null;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        return null;
    }

    /// <summary>Every TCP and UDP socket on the machine with its owning PID (no admin rights needed).</summary>
    public static List<RawConnection> ReadAll() => !OperatingSystem.IsWindows()
        ? LinuxSystem.ReadConnections()
        :
    [
        .. ReadTcp(AddressFamily.InterNetwork), .. ReadTcp(AddressFamily.InterNetworkV6),
        .. ReadUdp(AddressFamily.InterNetwork), .. ReadUdp(AddressFamily.InterNetworkV6),
    ];

    private static IEnumerable<RawConnection> ReadTcp(AddressFamily fam)
    {
        var v6 = fam == AddressFamily.InterNetworkV6;
        var data = ReadTable(IpHlpApi.GetExtendedTcpTable, v6 ? IpHlpApi.AF_INET6 : IpHlpApi.AF_INET, IpHlpApi.TCP_TABLE_OWNER_PID_ALL);
        if (data == null) yield break;
        var n = BinaryPrimitives.ReadInt32LittleEndian(data);
        var rowSize = v6 ? 56 : 24;
        for (var i = 0; i < n; i++)
        {
            var o = 4 + i * rowSize;
            if (o + rowSize > data.Length) yield break;
            var row = data.AsSpan(o, rowSize);
            IPAddress local, remote;
            int lport, rport, state, pid;
            if (v6)
            {
                local = new IPAddress(row[..16].ToArray(), BinaryPrimitives.ReadUInt32LittleEndian(row[16..]));
                lport = Port(BinaryPrimitives.ReadUInt32LittleEndian(row[20..]));
                remote = new IPAddress(row.Slice(24, 16).ToArray(), BinaryPrimitives.ReadUInt32LittleEndian(row[40..]));
                rport = Port(BinaryPrimitives.ReadUInt32LittleEndian(row[44..]));
                state = (int)BinaryPrimitives.ReadUInt32LittleEndian(row[48..]);
                pid = (int)BinaryPrimitives.ReadUInt32LittleEndian(row[52..]);
            }
            else
            {
                state = (int)BinaryPrimitives.ReadUInt32LittleEndian(row);
                local = new IPAddress(row.Slice(4, 4));
                lport = Port(BinaryPrimitives.ReadUInt32LittleEndian(row[8..]));
                remote = new IPAddress(row.Slice(12, 4));
                rport = Port(BinaryPrimitives.ReadUInt32LittleEndian(row[16..]));
                pid = (int)BinaryPrimitives.ReadUInt32LittleEndian(row[20..]);
            }
            var stateName = state is > 0 and < 13 ? TcpStates[state] : "?";
            var listening = state == 2;
            yield return new RawConnection("tcp", local, lport, listening ? null : remote, listening ? null : rport, stateName, pid);
        }
    }

    private static IEnumerable<RawConnection> ReadUdp(AddressFamily fam)
    {
        var v6 = fam == AddressFamily.InterNetworkV6;
        var data = ReadTable(IpHlpApi.GetExtendedUdpTable, v6 ? IpHlpApi.AF_INET6 : IpHlpApi.AF_INET, IpHlpApi.UDP_TABLE_OWNER_PID);
        if (data == null) yield break;
        var n = BinaryPrimitives.ReadInt32LittleEndian(data);
        var rowSize = v6 ? 28 : 12;
        for (var i = 0; i < n; i++)
        {
            var o = 4 + i * rowSize;
            if (o + rowSize > data.Length) yield break;
            var row = data.AsSpan(o, rowSize);
            IPAddress local;
            int lport, pid;
            if (v6)
            {
                local = new IPAddress(row[..16].ToArray(), BinaryPrimitives.ReadUInt32LittleEndian(row[16..]));
                lport = Port(BinaryPrimitives.ReadUInt32LittleEndian(row[20..]));
                pid = (int)BinaryPrimitives.ReadUInt32LittleEndian(row[24..]);
            }
            else
            {
                local = new IPAddress(row[..4]);
                lport = Port(BinaryPrimitives.ReadUInt32LittleEndian(row[4..]));
                pid = (int)BinaryPrimitives.ReadUInt32LittleEndian(row[8..]);
            }
            yield return new RawConnection("udp", local, lport, null, null, "BOUND", pid);
        }
    }

    public void Dispose() => _timer?.Dispose();
}

internal sealed class NullSink : IEventSink
{
    public void Emit(string type, object payload) { }
}
