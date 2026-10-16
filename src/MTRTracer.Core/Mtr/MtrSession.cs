using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MTRTracer.Core.Adapters;
using MTRTracer.Core.Dns;
using MTRTracer.Core.Geo;
using MTRTracer.Core.Logging;
using MTRTracer.Core.Probing;
using MTRTracer.Core.Protocol;
using TraceOptions = MTRTracer.Core.Protocol.TraceOptions;

namespace MTRTracer.Core.Mtr;

/// <summary>
/// One MTR run: resolves the target, binds to the chosen adapter, and probes every TTL on its own thread
/// (WinMTR model) with IcmpSendEcho2Ex. Aggregates per-hop and per-responder statistics, emits snapshots,
/// raw probe rows and route events, and survives network outages.
/// </summary>
public sealed class MtrSession : IAsyncDisposable
{
    private sealed class Responder
    {
        public required string Ip { get; init; }
        public readonly LatencyStats Stats = new();
    }

    private sealed class Hop
    {
        public readonly object Lock = new();
        public readonly LatencyStats Agg = new();
        public readonly Dictionary<string, Responder> Responders = new();
        public string LastStatus = "pending";
        public string? Primary;
        public int Unreachable;
        public bool UnreachableReported;
        public int Rounds;
    }

    private readonly TraceOptions _opts;
    private readonly string? _adapterId;
    private readonly ResolverService _resolver;
    private readonly IEventSink _sink;
    private readonly Action<IPAddress> _onDiscovered;
    private readonly Hop[] _hops;
    private readonly ManualResetEventSlim _running = new(true);
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = new();
    private readonly List<Thread> _workers = [];
    private readonly Analyzer _analyzer = new();

    private volatile IPAddress? _source;
    private IPAddress? _target;
    private byte[] _payload = [];
    private int _destTtl = int.MaxValue;
    private long _seq;
    private volatile bool _dirty;
    private Timer? _publishTimer;
    private Timer? _outageTimer;
    private Task? _tcpTask;
    private readonly LatencyStats _tcpStats = new();
    private int _tcpPort;
    private string _tcpStatus = "idle";
    private bool _destLossReported;
    private long _destLossSince;
    private bool _pmtuReported;
    private bool _destReachedReported;
    private int _activeWorkers;
    private long _lastNotesAt;
    private long _lastGeoAt;
    private bool _fakeReported;

    /// <summary>Lookup of enrichment data for the geo solver.</summary>
    public Func<string, HostInfoDto?>? HostLookup { get; set; }
    /// <summary>The user's physical location (manual or Cloudflare), anchor of the speed-of-light checks.</summary>
    public Func<GeoPoint?>? OriginProvider { get; set; }
    /// <summary>Raised once when the destination first answers (Cloudflare colo lookup etc.).</summary>
    public Action<IPAddress, string, IPAddress?>? DestinationReached { get; set; }
    /// <summary>Extra block attached to every snapshot (VPN mode).</summary>
    public Func<object?>? SnapshotExtra { get; set; }
    /// <summary>Probes leave through a tunnel adapter: watch for replies the tunnel fakes locally.</summary>
    public bool TunnelAdapter { get; set; }
    public bool FakeReplies => _fakeReported;
    public RouteGeoDto? LastGeo { get; private set; }

    /// <summary>Best RTT of the destination hop, if reached.</summary>
    public double? DestinationBest()
    {
        var d = Volatile.Read(ref _destTtl);
        if (d == int.MaxValue || d >= _hops.Length) return null;
        var h = _hops[d];
        lock (h.Lock) return Num.N(h.Agg.Best);
    }
    private List<NoteDto> _notes = [];

    public string Id { get; }
    public TraceStateDto State { get; }
    public ProbeLog Log { get; }
    public TraceOptions Options => _opts;
    public IPAddress? Target => _target;
    public ResolveResult? Resolution { get; private set; }

    public MtrSession(string id, string input, TraceOptions opts, string? adapterId, ResolverService resolver,
        IEventSink sink, string? logDirectory, Action<IPAddress> onDiscovered)
    {
        Id = id;
        _opts = opts.Clone().Normalize();
        _adapterId = adapterId;
        _resolver = resolver;
        _sink = sink;
        _onDiscovered = onDiscovered;
        _hops = Enumerable.Range(0, _opts.MaxHops + 1).Select(_ => new Hop()).ToArray();
        Log = new ProbeLog(logDirectory, id);
        State = new TraceStateDto { SessionId = id, State = "resolving", Input = input, AdapterId = adapterId };
    }

    private void EmitState() => _sink.Emit("trace.state", State);

    private void Event(string level, string code, string message, int? ttl = null, string? ip = null)
    {
        var e = new TraceEventDto(IcmpProber.NowUs() / 1000, level, code, message, ttl, ip);
        Log.Add(e);
        _sink.Emit("trace.event", e);
    }

    private void AddRow(ProbeRowDto row)
    {
        Log.Add(row);
        _sink.Emit("trace.probe", row);
    }

    public async Task StartAsync()
    {
        EmitState();
        var parsed = TargetParser.Parse(State.Input);
        if (parsed == null)
        {
            Fail("bad-target", "Не удалось распознать адрес. Введите IP, домен или URL.");
            return;
        }
        State.Host = parsed.Host;
        State.Port = parsed.Port;
        if (parsed.Port is { } p) _opts.TcpPort = p;

        // Adapter DNS servers drive the fallback resolver chain.
        if (_adapterId != null && AdapterService.Find(_adapterId) is { } ni)
        {
            try { _resolver.AdapterDns = ni.GetIPProperties().DnsAddresses.ToList(); } catch { }
        }
        else
        {
            _resolver.AdapterDns = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => { try { return n.GetIPProperties().DnsAddresses.ToList(); } catch { return []; } })
                .Distinct().ToList();
        }

        ResolveResult res;
        try
        {
            res = await _resolver.ResolveTargetAsync(parsed.Host, _opts.IpVersion, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Resolution = res;
        _sink.Emit("trace.resolved", new
        {
            sessionId = Id,
            input = State.Input,
            host = res.Host,
            address = res.Address == null ? null : AdapterService.Format(res.Address),
            addresses = res.Addresses,
            via = res.Via,
            ms = Num.N(res.Ms),
            attempts = res.Attempts.Select(a => new { resolver = a.Resolver, via = a.Via, ms = Num.N(a.Ms), ok = a.Ok, error = a.Error, addresses = a.Addresses }),
        });
        Log.AddMeta(new { type = "resolved", res.Host, address = res.Address?.ToString(), res.Via, res.Ms, res.Addresses });
        if (res.Address == null)
        {
            var reason = res.Attempts.LastOrDefault()?.Error ?? "нет ответа";
            Fail("resolve-failed", $"Не удалось получить IP для «{parsed.Host}»: {reason}.");
            return;
        }
        if (res.Via is "adapter-dns" or "public-dns")
            Event("warn", "dns-fallback", $"Системный DNS не ответил, адрес получен через {res.Attempts.Last(a => a.Ok).Resolver}.");

        _target = res.Address;
        State.TargetIp = AdapterService.Format(_target);
        State.Family = _target.AddressFamily == AddressFamily.InterNetworkV6 ? "6" : "4";

        try
        {
            _source = AdapterService.ResolveSource(_adapterId, _target);
        }
        catch (AdapterException e)
        {
            Fail(e.Code, e.Message);
            return;
        }

        var effectiveSource = _source ?? AdapterService.SystemSourceFor(_target);
        State.SourceIp = effectiveSource == null ? null : AdapterService.Format(effectiveSource);
        if (_adapterId != null && AdapterService.Find(_adapterId) is { } chosen)
        {
            int ifIndex = 0;
            try
            {
                var ip = chosen.GetIPProperties();
                ifIndex = _target.AddressFamily == AddressFamily.InterNetworkV6 ? ip.GetIPv6Properties().Index : ip.GetIPv4Properties().Index;
            }
            catch { }
            if (AdapterService.LookupRoute(_target, ifIndex, _source) == null)
                Event("warn", "no-route", $"У адаптера «{chosen.Name}» нет маршрута до {State.TargetIp}. Пакеты могут не уйти; попробуйте режим «Авто».");
        }

        _payload = IcmpProber.BuildPayload(_opts.PacketSize, _opts.Payload);

        // One permission check up front instead of an error from every TTL thread (Linux without ping sockets).
        try
        {
            using var check = IcmpProber.Create(_target.AddressFamily, 64);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SocketException or InvalidOperationException)
        {
            Fail("icmp-permission", e.Message);
            return;
        }
        _onDiscovered(_target);

        State.State = "running";
        State.StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        EmitState();
        Log.AddMeta(new { type = "start", State.Input, State.TargetIp, State.SourceIp, State.AdapterId, options = _opts });
        Event("info", "start", $"Трассировка {res.Host} [{State.TargetIp}] с {State.SourceIp ?? "системного адреса"}: интервал {_opts.IntervalMs} мс, {_opts.PacketSize} байт, до {_opts.MaxHops} хопов.");

        _clock.Start();
        for (var ttl = 1; ttl <= _opts.MaxHops; ttl++)
        {
            var t = ttl;
            var thread = new Thread(() => Worker(t)) { IsBackground = true, Name = $"mtr-ttl-{t}", Priority = ThreadPriority.AboveNormal };
            _workers.Add(thread);
            Interlocked.Increment(ref _activeWorkers);
            thread.Start();
        }
        _publishTimer = new Timer(_ => Publish(false), null, 100, 100);
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailability;
    }

    private void Fail(string code, string message)
    {
        State.State = "error";
        State.ErrorCode = code;
        State.Error = message;
        EmitState();
        Event("error", code, message);
    }

    private void Worker(int ttl)
    {
        var hop = _hops[ttl];
        try
        {
            using var prober = IcmpProber.Create(_target!.AddressFamily, Math.Max(_opts.PacketSize, 64));
            // Small stagger so 30 probes do not leave in the same microsecond (router ICMP rate limits).
            if (_cts.Token.WaitHandle.WaitOne(Math.Min(ttl * 7, 250))) return;
            while (!_cts.IsCancellationRequested)
            {
                _running.Wait(_cts.Token);
                if (ttl > Volatile.Read(ref _destTtl)) break;
                if (_opts.MaxRounds > 0 && hop.Rounds >= _opts.MaxRounds) break;
                var started = Stopwatch.GetTimestamp();
                hop.Rounds++;
                for (var b = 0; b < _opts.Burst && !_cts.IsCancellationRequested; b++)
                {
                    if (!_running.IsSet) break;
                    var seq = Interlocked.Increment(ref _seq);
                    var source = _source;
                    lock (hop.Lock) hop.Agg.AddSent();
                    var r = prober.Send(_target, source, ttl, _opts.TimeoutMs, _payload, _opts.DontFragment);
                    Record(ttl, hop, seq, source, r);
                }
                var spent = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var wait = (int)Math.Max(0, _opts.IntervalMs - spent);
                if (wait > 0 && _cts.Token.WaitHandle.WaitOne(wait)) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Event("error", "worker-failed", $"Ошибка потока TTL {ttl}: {e.Message}", ttl);
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeWorkers) == 0 && !_cts.IsCancellationRequested)
            {
                State.State = "finished";
                EmitState();
                Publish(true);
                Event("info", "finished", $"Завершено: {_opts.MaxRounds} раундов.");
            }
        }
    }

    private void Record(int ttl, Hop hop, long seq, IPAddress? source, ProbeResult r)
    {
        var fromStr = r.From == null ? null : AdapterService.Format(r.From);
        var reachedTarget = r.From != null && r.From.Equals(_target) && r.Status == ProbeStatus.Ok;
        var statusCode = IcmpProber.StatusCode(r.Status);
        var isUnreachable = r.Status is ProbeStatus.NetUnreachable or ProbeStatus.HostUnreachable or ProbeStatus.Prohibited
            or ProbeStatus.ProtocolUnreachable or ProbeStatus.PortUnreachable or ProbeStatus.BadRoute;

        AddRow(new ProbeRowDto(r.TimestampUs / 1000, hop.Rounds, seq, ttl,
            source == null ? State.SourceIp : AdapterService.Format(source), State.TargetIp!, fromStr,
            statusCode, r.RawStatus, Num.N(r.RttMs, 3), r.KernelRttMs, _payload.Length, "icmp"));

        if (r.Status == ProbeStatus.PacketTooBig && !_pmtuReported)
        {
            _pmtuReported = true;
            Event("warn", "packet-too-big", $"Хоп {ttl}: пакет {_opts.PacketSize} байт с флагом DF больше MTU канала. Уменьшите размер или снимите DF.", ttl, fromStr);
        }

        bool newResponder = false;
        string? previousPrimary = null;
        lock (hop.Lock)
        {
            hop.LastStatus = statusCode;
            if (fromStr != null && !double.IsNaN(r.RttMs))
            {
                hop.Agg.AddReply(r.RttMs);
                if (!hop.Responders.TryGetValue(fromStr, out var resp))
                {
                    resp = new Responder { Ip = fromStr };
                    hop.Responders[fromStr] = resp;
                    newResponder = true;
                }
                resp.Stats.AddSent();
                resp.Stats.AddReply(r.RttMs);
                previousPrimary = hop.Primary;
                hop.Primary = hop.Responders.Values.MaxBy(x => x.Stats.Recv)!.Ip;
                if (isUnreachable && !reachedTarget) hop.Unreachable++;
            }
            else
            {
                hop.Agg.AddLoss();
            }
        }

        if (newResponder && r.From != null)
        {
            _onDiscovered(r.From);
            var replies = hop.Agg.Recv;
            if (hop.Responders.Count > 1 && replies > 3)
                Event("warn", "route-change", $"Хоп {ttl}: новый узел {fromStr} (ранее {previousPrimary ?? "—"}). Возможна смена маршрута или балансировка (ECMP).", ttl, fromStr);
        }
        else if (previousPrimary != null && hop.Primary != previousPrimary)
        {
            Event("info", "primary-change", $"Хоп {ttl}: основным узлом стал {hop.Primary} вместо {previousPrimary}.", ttl, hop.Primary);
        }

        if (reachedTarget)
        {
            var prev = Volatile.Read(ref _destTtl);
            if (ttl < prev)
            {
                Interlocked.CompareExchange(ref _destTtl, ttl, prev);
                if (!_destReachedReported)
                {
                    _destReachedReported = true;
                    Event("info", "destination", $"Цель {State.TargetIp} достигнута за {ttl} хоп(ов).", ttl, fromStr);
                    if (State.Host != null && State.Host != State.TargetIp) DestinationReached?.Invoke(_target!, State.Host, _source);
                }
                // A proxy tunnel (Xray, sing-box, tun2socks) answers echo requests itself: the "destination" replies
                // at TTL 1 in well under a millisecond. Those numbers say nothing about the real path.
                if (ttl == 1 && TunnelAdapter && r.RttMs < 1.0 && !_fakeReported)
                {
                    _fakeReported = true;
                    Event("error", "fake-replies", $"Цель «ответила» на первом же хопе за {r.RttMs:0.00} мс: это отвечает сам VPN-туннель, а не {State.TargetIp}. Через прокси-туннель трассировка по хопам невозможна. Включите VPN-режим.", ttl, fromStr);
                }
            }
        }
        else if (isUnreachable && hop.Unreachable >= 3 && !hop.UnreachableReported)
        {
            hop.UnreachableReported = true;
            if (ttl < Volatile.Read(ref _destTtl)) Interlocked.Exchange(ref _destTtl, ttl);
            Event("error", "dest-unreachable", $"Хоп {ttl} ({fromStr}) сообщает: {StatusText(r.Status)}. Дальше маршрут не идёт.", ttl, fromStr);
        }

        if (ttl == Volatile.Read(ref _destTtl))
            TrackDestinationLoss(hop, fromStr != null && !double.IsNaN(r.RttMs));

        _dirty = true;
    }

    private void TrackDestinationLoss(Hop hop, bool replied)
    {
        if (!replied && hop.Agg.ConsecutiveLoss == 3 && !_destLossReported)
        {
            _destLossReported = true;
            _destLossSince = IcmpProber.NowUs() / 1000;
            Event("error", "loss-burst", "Цель перестала отвечать: 3 пакета подряд потеряны.");
        }
        else if (replied && _destLossReported)
        {
            _destLossReported = false;
            var dur = IcmpProber.NowUs() / 1000 - _destLossSince;
            Event("info", "recovered", $"Связь с целью восстановлена через {dur / 1000.0:0.0} с.");
        }
    }

    public static string StatusText(ProbeStatus s) => s switch
    {
        ProbeStatus.NetUnreachable => "сеть недоступна",
        ProbeStatus.HostUnreachable => "узел недоступен",
        ProbeStatus.Prohibited => "доступ запрещён фильтром",
        ProbeStatus.ProtocolUnreachable => "протокол недоступен / запрещено",
        ProbeStatus.PortUnreachable => "порт недоступен",
        ProbeStatus.BadRoute => "неверный маршрут",
        _ => s.ToString(),
    };

    private void Publish(bool force)
    {
        if (!force && !_dirty) return;
        _dirty = false;
        try
        {
            var snap = Snapshot();
            _sink.Emit("trace.hops", snap);
            var now = Environment.TickCount64;
            if (HostLookup != null && (force || now - _lastGeoAt > 1000))
            {
                _lastGeoAt = now;
                // The destination AS may only become known later (RIPEstat); re-offer the Cloudflare colo check.
                if (_destReachedReported && _target != null && State.Host != null && State.Host != State.TargetIp)
                    DestinationReached?.Invoke(_target, State.Host, _source);
                LastGeo = SolveGeo(snap);
                _sink.Emit("route.geo", LastGeo);
            }
            MaybeStartTcpFallback();
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    public RouteGeoDto SolveGeo(HopsSnapshotDto snap)
    {
        var input = snap.Hops.Select(h => new HopGeoInput(h.Ttl, h.Primary, h.Best, h.Recv, h.Primary == null ? null : HostLookup?.Invoke(h.Primary), h.IsDestination)).ToList();
        return RouteGeoSolver.Solve(Id, OriginProvider?.Invoke(), input);
    }

    public HopsSnapshotDto Snapshot()
    {
        var dest = Volatile.Read(ref _destTtl);
        var highest = 0;
        for (var t = 1; t <= _opts.MaxHops; t++)
            if (_hops[t].Agg.Recv > 0) highest = t;
        var visible = dest != int.MaxValue ? dest : Math.Min(_opts.MaxHops, Math.Max(highest + 1, 1));
        if (dest == int.MaxValue && _hops[1].Rounds >= 3 && highest < _opts.MaxHops)
            visible = Math.Min(_opts.MaxHops, Math.Max(visible, highest + 3));

        var hops = new List<HopDto>(visible);
        var round = 0;
        for (var t = 1; t <= visible; t++)
        {
            var h = _hops[t];
            lock (h.Lock)
            {
                round = Math.Max(round, h.Rounds);
                var a = h.Agg;
                var total = Math.Max(1, h.Responders.Values.Sum(x => x.Stats.Recv));
                hops.Add(new HopDto(
                    t,
                    h.Primary,
                    h.Responders.Values
                        .OrderByDescending(x => x.Stats.Recv)
                        .Select(x => new ResponderDto(x.Ip, x.Stats.Recv, Math.Round(x.Stats.Recv * 100.0 / total, 1), Num.N(x.Stats.Avg), Num.N(x.Stats.Best), Num.N(x.Stats.Last)))
                        .ToList(),
                    a.Sent, a.Recv, Math.Round(a.LossPct, 1),
                    Num.N(a.Last), Num.N(a.Best), Num.N(a.Avg), Num.N(a.Worst), Num.N(a.StDev), Num.N(a.GMean),
                    Num.N(a.Jitter), Num.N(a.JAvg), Num.N(a.JMax), Num.N(a.JInt),
                    h.LastStatus,
                    a.History().Select(v => Num.N(v, 2)).ToArray(),
                    t == dest && dest != int.MaxValue && !h.UnreachableReported,
                    a.ConsecutiveLoss));
            }
        }

        TcpStatsDto? tcp = null;
        if (_tcpTask != null)
        {
            lock (_tcpStats)
                tcp = new TcpStatsDto(_tcpPort, _tcpStats.Sent, _tcpStats.Recv, Math.Round(_tcpStats.LossPct, 1),
                    Num.N(_tcpStats.Last), Num.N(_tcpStats.Best), Num.N(_tcpStats.Avg), Num.N(_tcpStats.Worst), _tcpStatus);
        }

        var now = Environment.TickCount64;
        if (now - _lastNotesAt > 1000)
        {
            _lastNotesAt = now;
            _notes = _analyzer.Analyze(hops, dest == int.MaxValue ? null : dest, tcp);
            if (State.Mode == "vpn")
                _notes.Insert(0, new NoteDto("info", "vpn-scope", "VPN-режим: хопы и разбор относятся к участку до сервера VPN. Задержка до самой цели в строке «через туннель».", null));
            if (_fakeReported)
                _notes.Insert(0, new NoteDto("error", "fake-replies", "Ответы подделаны VPN-туннелем: задержки и хопы в таблице не настоящие. Для туннеля используйте VPN-режим.", 1));
        }
        return new HopsSnapshotDto(Id, round, dest == int.MaxValue ? null : dest, hops, tcp, _notes, _clock.ElapsedMilliseconds, SnapshotExtra?.Invoke());
    }

    private void MaybeStartTcpFallback()
    {
        if (_tcpTask != null || !_opts.TcpFallback || _target == null) return;
        if (Volatile.Read(ref _destTtl) != int.MaxValue) return;
        // Give ICMP a few rounds; probes at the last TTLs need the full timeout to be counted.
        if (_hops[1].Rounds < 4) return;
        _tcpTask = Task.Run(TcpLoopAsync);
        Event("warn", "tcp-fallback", $"Цель не отвечает на ICMP. Включён TCP-пинг (порт {_opts.TcpPort}): время установки соединения.");
    }

    private async Task TcpLoopAsync()
    {
        var ports = new[] { _opts.TcpPort, 443, 80, 22 }.Distinct().ToArray();
        var portIndex = 0;
        var misses = 0;
        _tcpPort = ports[0];
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { _running.Wait(ct); } catch (OperationCanceledException) { break; }
            var started = Stopwatch.GetTimestamp();
            var r = await TcpPinger.PingAsync(_target!, _tcpPort, _source, _opts.TimeoutMs, ct);
            lock (_tcpStats)
            {
                _tcpStats.AddSent();
                if (r.Reachable) _tcpStats.AddReply(r.RttMs); else _tcpStats.AddLoss();
                _tcpStatus = r.Status;
            }
            AddRow(new ProbeRowDto(IcmpProber.NowUs() / 1000, _tcpStats.Sent, Interlocked.Increment(ref _seq), 0,
                State.SourceIp, State.TargetIp!, r.Reachable ? State.TargetIp : null, "tcp-" + r.Status, 0,
                Num.N(r.RttMs), 0, 0, "tcp:" + _tcpPort));
            if (!r.Reachable && _tcpStats.Recv == 0 && ++misses >= 2 && portIndex < ports.Length - 1)
            {
                misses = 0;
                _tcpPort = ports[++portIndex];
            }
            _dirty = true;
            var wait = _opts.IntervalMs - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (wait > 0)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(wait), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    public void SetPaused(bool paused)
    {
        if (State.State is "error" or "stopped" or "finished" or "resolving") return;
        if (paused)
        {
            _running.Reset();
            State.State = "paused";
            Event("info", "paused", "Пауза.");
        }
        else
        {
            if (State.State == "waiting-network") return;
            _running.Set();
            State.State = "running";
            Event("info", "resumed", "Продолжено.");
        }
        EmitState();
    }

    // ---------- network outage handling ----------

    private void OnNetworkAvailability(object? sender, NetworkAvailabilityEventArgs e) => CheckNetwork();
    private void OnNetworkChanged(object? sender, EventArgs e) => CheckNetwork();

    private readonly object _netLock = new();

    private void CheckNetwork()
    {
        if (_target == null || _cts.IsCancellationRequested) return;
        lock (_netLock)
        {
            var ok = TryResolveSource(out var newSource, out var reason);
            if (!ok && State.State is "running" or "paused")
            {
                _running.Reset();
                State.State = "waiting-network";
                State.OutageSince = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                EmitState();
                Event("error", "network-down", $"Сеть недоступна: {reason}. Трассировка приостановлена и продолжится автоматически.");
                _outageTimer ??= new Timer(_ => CheckNetwork(), null, 1000, 1000);
            }
            else if (ok && State.State == "waiting-network")
            {
                var since = State.OutageSince ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var dur = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - since) / 1000.0;
                ApplySource(newSource);
                State.OutageSince = null;
                State.State = "running";
                _outageTimer?.Dispose();
                _outageTimer = null;
                _running.Set();
                EmitState();
                Event("info", "network-up", $"Сеть восстановлена через {dur:0.0} с. Источник: {State.SourceIp ?? "системный"}.");
            }
            else if (ok && !Equals(newSource, _source) && _adapterId != null)
            {
                ApplySource(newSource);
                EmitState();
                Event("warn", "source-changed", $"IP-адрес адаптера изменился, пакеты теперь уходят с {State.SourceIp}.");
            }
        }
    }

    private void ApplySource(IPAddress? src)
    {
        _source = src;
        var eff = src ?? AdapterService.SystemSourceFor(_target!);
        State.SourceIp = eff == null ? null : AdapterService.Format(eff);
    }

    private bool TryResolveSource(out IPAddress? source, out string reason)
    {
        source = null;
        reason = "";
        try
        {
            if (_adapterId != null)
            {
                source = AdapterService.ResolveSource(_adapterId, _target!);
                return true;
            }
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                reason = "нет активных подключений";
                return false;
            }
            if (AdapterService.SystemSourceFor(_target!) == null)
            {
                reason = "нет маршрута до цели";
                return false;
            }
            return true;
        }
        catch (AdapterException e)
        {
            reason = e.Message.TrimEnd('.');
            return false;
        }
    }

    public async Task StopAsync()
    {
        if (_cts.IsCancellationRequested) return;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailability;
        var wasRunning = State.State is "running" or "paused" or "waiting-network";
        _cts.Cancel();
        _running.Set();
        _publishTimer?.Dispose();
        _outageTimer?.Dispose();
        if (wasRunning)
        {
            State.State = "stopped";
            Publish(true);
            EmitState();
            Event("info", "stopped", "Остановлено.");
        }
        if (_tcpTask != null)
        {
            try { await _tcpTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await Log.DisposeAsync();
        _cts.Dispose();
    }
}
