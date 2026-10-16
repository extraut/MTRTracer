using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.Json;
using MTRTracer.Core.Adapters;
using MTRTracer.Core.Connections;
using MTRTracer.Core.Dns;
using MTRTracer.Core.Export;
using MTRTracer.Core.Geo;
using MTRTracer.Core.History;
using MTRTracer.Core.Logging;
using MTRTracer.Core.Mtr;
using MTRTracer.Core.Probing;
using MTRTracer.Core.Protocol;
using MTRTracer.Core.Updates;

namespace MTRTracer.Core;

public sealed class OriginSetting
{
    /// <summary>auto (Cloudflare, by the physical adapter's public IP) | manual</summary>
    public string Mode { get; set; } = "auto";
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public string? Label { get; set; }
}

public sealed class AppSettings
{
    public string? AdapterId { get; set; }
    public bool AdapterChosen { get; set; }
    public bool VpnMode { get; set; }
    /// <summary>Manually entered VPN server (for UDP tunnels whose server cannot be seen in the TCP table).</summary>
    public string? VpnServer { get; set; }
    public OriginSetting Origin { get; set; } = new();
    public TraceOptions Options { get; set; } = new();
    public List<string> Recent { get; set; } = [];
    public JsonElement? Ui { get; set; }
    /// <summary>"ru" | "en"; null = by the system language.</summary>
    public string? Language { get; set; }
    public GeoProviders Providers { get; set; } = new();
    public GeoDbSetting GeoDb { get; set; } = new();
    /// <summary>GitHub repository checked for updates ("owner/name"); forks can point it at themselves.</summary>
    public string UpdateRepo { get; set; } = UpdateChecker.DefaultRepo;
}

public sealed class CommandException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Everything the UI can ask for, independent of the window technology. The WPF host only forwards
/// JSON messages here and pushes events back.
/// </summary>
public sealed class AppController : IAsyncDisposable
{
    private readonly IEventSink _sink;
    private readonly string _settingsPath;
    private readonly HttpClient _http;
    private readonly ResolverService _resolver = new();
    private readonly DbIpDatabase _db;
    private readonly IpApiClient _ipApi;
    private readonly HostInfoService _hosts;
    private readonly ConnectionMonitor _connections;
    private readonly HistoryStore? _history;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private MtrSession? _session;
    private VpnOverlay? _overlay;
    private long? _runId;
    private Timer? _historyTimer;
    private Timer? _trafficTimer;
    private int _sessionCounter;
    private CloudflareMeta? _self;
    private string? _selfVia;
    private string? _selfContext;
    private string? _selfNote;
    private readonly SemaphoreSlim _selfLock = new(1, 1);

    public string DataDir { get; }
    public string SessionsDir { get; }
    public AppSettings Settings { get; private set; }

    public AppController(IEventSink sink, string dataDir)
    {
        _sink = sink;
        DataDir = dataDir;
        SessionsDir = Path.Combine(dataDir, "sessions");
        Directory.CreateDirectory(dataDir);
        var geoDir = Path.Combine(dataDir, "geo");
        Directory.CreateDirectory(geoDir);
        _settingsPath = Path.Combine(dataDir, "settings.json");
        Settings = LoadSettings();
        Loc.Language = Settings.Language ?? Loc.SystemDefault();
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"MTRTracer/{Version}");
        _db = new DbIpDatabase(geoDir, Settings.GeoDb) { Enabled = Settings.Providers.OfflineDb };
        _ipApi = new IpApiClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, geoDir);
        _hosts = new HostInfoService(_resolver, _db, _ipApi, geoDir, sink)
        {
            Online = Settings.Options.GeoOnline,
            ResolveNames = Settings.Options.ResolveNames,
            Providers = Settings.Providers,
        };
        _ipApi.Enabled = Settings.Options.GeoOnline;
        _connections = new ConnectionMonitor(sink, _hosts);
        try
        {
            _history = new HistoryStore(Path.Combine(dataDir, "history.db"));
            _history.Prune(TimeSpan.FromDays(180));
        }
        catch (Exception e)
        {
            _history = null;
            System.Diagnostics.Debug.WriteLine(e);
        }
        _db.Changed += () =>
        {
            EmitGeoStatus();
            if (_db.Ready) _hosts.RefreshFromDb();
        };
        _ = Task.Run(() => _db.EnsureAsync(_http, CancellationToken.None));
    }

    /// <summary>Language from settings.json before the controller exists (for host messages shown early).</summary>
    public static string PeekLanguage(string dataDir)
    {
        try
        {
            var path = Path.Combine(dataDir, "settings.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String) return Loc.Normalize(l.GetString());
            }
        }
        catch { }
        return Loc.SystemDefault();
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), Json.Options);
                if (s != null)
                {
                    s.Options.Normalize();
                    s.Origin ??= new OriginSetting();
                    s.Providers ??= new GeoProviders();
                    s.GeoDb = (s.GeoDb ?? new GeoDbSetting()).Normalize();
                    s.UpdateRepo = UpdateChecker.NormalizeRepo(s.UpdateRepo);
                    if (s.Language != null) s.Language = Loc.Normalize(s.Language);
                    return s;
                }
            }
        }
        catch { }
        return new AppSettings();
    }

    private void SaveSettings()
    {
        try { File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings, Json.Options)); } catch { }
    }

    private object GeoStatus() => new
    {
        db = _db.Status,
        progress = Math.Round(_db.Progress, 3),
        stage = _db.Stage,
        dbDate = _db.DbDate,
        error = _db.Error,
        online = _hosts.Online,
        onlineError = _ipApi.LastError,
        active = _db.ActiveId,
        enabled = _db.Enabled,
        autoUpdate = Settings.GeoDb.AutoUpdate,
    };

    public static string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.1.1";

    private void EmitGeoStatus() => _sink.Emit("geo.status", GeoStatus());

    private static T? Arg<T>(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return default;
        return v.Deserialize<T>(Json.Options);
    }

    private static bool Has(JsonElement p, string name) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out _);

    private void ApplyOptions(TraceOptions o)
    {
        Settings.Options = o.Normalize();
        _hosts.Online = o.GeoOnline;
        _ipApi.Enabled = o.GeoOnline;
        _hosts.ResolveNames = o.ResolveNames;
        EmitGeoStatus();
    }

    public async Task<object?> HandleAsync(string type, JsonElement p)
    {
        switch (type)
        {
            case "app.info":
                return new
                {
                    version = Version,
                    language = Loc.Language,
                    systemLanguage = Loc.SystemDefault(),
                    repo = Settings.UpdateRepo,
                    isAdmin = Environment.IsPrivilegedProcess,
                    platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other",
                    dataDir = DataDir,
                    sessionsDir = SessionsDir,
                    machine = Environment.MachineName,
                    os = Environment.OSVersion.VersionString,
                    history = _history != null,
                };

            case "settings.get":
                return Settings;

            case "settings.set":
            {
                if (Arg<TraceOptions>(p, "options") is { } o) ApplyOptions(o);
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("ui", out var ui)) Settings.Ui = ui.Clone();
                if (Arg<OriginSetting>(p, "origin") is { } origin)
                {
                    Settings.Origin = origin;
                    _sink.Emit("geo.origin", OriginInfo());
                }
                if (Has(p, "vpnMode"))
                {
                    Settings.VpnMode = Arg<bool>(p, "vpnMode");
                    _ = RefreshSelfAsync(false);
                }
                if (Has(p, "language"))
                {
                    Settings.Language = Arg<string>(p, "language") is { } lang ? Loc.Normalize(lang) : null;
                    Loc.Language = Settings.Language ?? Loc.SystemDefault();
                }
                if (Arg<GeoProviders>(p, "providers") is { } providers)
                {
                    Settings.Providers = providers;
                    _hosts.Providers = providers;
                    _db.Enabled = providers.OfflineDb;
                    if (providers.OfflineDb) _hosts.RefreshFromDb();
                    EmitGeoStatus();
                }
                if (Arg<GeoDbSetting>(p, "geoDb") is { } gdb)
                {
                    Settings.GeoDb.AutoUpdate = gdb.AutoUpdate;
                    Settings.GeoDb.UrlTemplate = gdb.UrlTemplate;
                    Settings.GeoDb.Normalize();
                    EmitGeoStatus();
                }
                if (Has(p, "updateRepo")) Settings.UpdateRepo = UpdateChecker.NormalizeRepo(Arg<string>(p, "updateRepo"));
                if (Has(p, "vpnServer")) Settings.VpnServer = string.IsNullOrWhiteSpace(Arg<string>(p, "vpnServer")) ? null : Arg<string>(p, "vpnServer")!.Trim();
                SaveSettings();
                return Settings;
            }

            case "adapters.list":
            {
                var adapters = await Task.Run(AdapterService.List);
                var tunnels = await Task.Run(() => adapters.Where(a => a.Kind == "vpn" && a.Status == "up" && a.HasRoute)
                    .Select(a => TunnelDetector.Detect(a.Id)).Where(t => t != null).ToList());
                return new { adapters, selectedId = Settings.AdapterId, chosen = Settings.AdapterChosen, vpnMode = Settings.VpnMode, tunnels };
            }

            case "adapters.select":
            {
                var id = Arg<string>(p, "adapterId");
                if (id != null && AdapterService.Find(id) == null) throw new CommandException("adapter-missing", Loc.S("Адаптер не найден.", "Adapter not found."));
                Settings.AdapterId = id;
                Settings.AdapterChosen = true;
                if (Has(p, "vpnMode")) Settings.VpnMode = Arg<bool>(p, "vpnMode");
                SaveSettings();
                InvalidateSelf();
                return new { adapterId = id, vpnMode = Settings.VpnMode };
            }

            case "adapters.watch":
            {
                var on = Arg<bool>(p, "on");
                _trafficTimer?.Dispose();
                _trafficTimer = on ? new Timer(_ => EmitTraffic(), null, 0, 1000) : null;
                return null;
            }

            case "tunnel.detect":
            {
                var id = Arg<string>(p, "adapterId") ?? TunnelAdapterId() ?? throw new CommandException("no-tunnel", Loc.S("VPN-адаптер не найден.", "VPN adapter not found."));
                return await Task.Run(() => TunnelDetector.Detect(id));
            }

            case "trace.start":
            {
                var target = Arg<string>(p, "target") ?? throw new CommandException("bad-target", Loc.S("Введите адрес.", "Enter an address."));
                if (Arg<TraceOptions>(p, "options") is { } o) ApplyOptions(o);
                if (Has(p, "vpnMode")) Settings.VpnMode = Arg<bool>(p, "vpnMode");
                Settings.Recent.RemoveAll(r => string.Equals(r, target.Trim(), StringComparison.OrdinalIgnoreCase));
                Settings.Recent.Insert(0, target.Trim());
                if (Settings.Recent.Count > 12) Settings.Recent.RemoveRange(12, Settings.Recent.Count - 12);
                SaveSettings();
                return new { sessionId = await StartSessionAsync(target.Trim()) };
            }

            case "trace.stop":
                if (_overlay != null)
                {
                    await _overlay.DisposeAsync();
                    _overlay = null;
                }
                if (_session != null) await _session.StopAsync();
                await FinishRunAsync();
                return null;

            case "trace.pause":
                _session?.SetPaused(Arg<bool>(p, "paused"));
                return null;

            case "trace.reset":
            {
                var input = _session?.State.OverlayInput ?? _session?.State.Input ?? throw new CommandException("no-session", Loc.S("Нет активной трассировки.", "No active trace."));
                return new { sessionId = await StartSessionAsync(input) };
            }

            case "trace.pmtu":
            {
                var s = _session ?? throw new CommandException("no-session", Loc.S("Сначала запустите трассировку.", "Start a trace first."));
                var target = s.Target ?? throw new CommandException("no-target", Loc.S("Цель ещё не определена.", "The target is not resolved yet."));
                var src = s.State.SourceIp != null && s.State.AdapterId != null ? IPAddress.Parse(s.State.SourceIp) : null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var mtu = await Task.Run(() => PmtuProbe.Discover(target, src, 1500, CancellationToken.None));
                return new { mtu, ms = Math.Round(sw.Elapsed.TotalMilliseconds) };
            }

            case "trace.dnsBench":
            {
                var host = _session?.State.OverlayInput ?? _session?.State.Host ?? Arg<string>(p, "host") ?? "example.com";
                host = TargetParser.Parse(host)?.Host ?? host;
                if (IPAddress.TryParse(host, out _)) host = "example.com";
                return new { host, results = await _resolver.BenchmarkAsync(host, CancellationToken.None) };
            }

            case "geo.status":
                return GeoStatus();

            case "geo.download":
                _ = Task.Run(() => _db.DownloadReleaseAsync(_http, Arg<string>(p, "month"), prune: false, CancellationToken.None));
                return null;

            case "geo.db.list":
                return new { releases = _db.Releases(), active = _db.ActiveId, pinned = Settings.GeoDb.Active, setting = Settings.GeoDb, dir = _db.Directory };

            case "geo.db.available":
                return await _db.AvailableAsync(_http, Math.Clamp(Arg<int?>(p, "count") ?? 6, 1, 24), CancellationToken.None);

            case "geo.db.use":
                _db.Use(Arg<string>(p, "id"));
                Settings.GeoDb.Active = _db.Setting.Active;
                SaveSettings();
                _hosts.RefreshFromDb();
                return new { active = _db.ActiveId };

            case "geo.db.delete":
            {
                var id = Arg<string>(p, "id") ?? throw new CommandException("bad-id", "id");
                _db.Delete(id);
                Settings.GeoDb.Active = _db.Setting.Active;
                SaveSettings();
                return new { active = _db.ActiveId };
            }

            case "geo.db.import":
            {
                var kind = _db.Import(Arg<string>(p, "path") ?? throw new CommandException("bad-path", Loc.S("Укажите файл.", "Choose a file.")));
                Settings.GeoDb.Active = _db.Setting.Active;
                SaveSettings();
                _hosts.RefreshFromDb();
                return new { kind, active = _db.ActiveId };
            }

            case "geo.db.fetch":
            {
                var kind = await _db.FetchUrlAsync(_http, Arg<string>(p, "url") ?? "", CancellationToken.None);
                Settings.GeoDb.Active = _db.Setting.Active;
                SaveSettings();
                _hosts.RefreshFromDb();
                return new { kind, active = _db.ActiveId };
            }

            case "geo.cache.stats":
                return _hosts.CacheStats();

            case "geo.cache.clear":
                _hosts.ClearCaches();
                InvalidateSelf();
                _ = RefreshSelfAsync(true);
                return _hosts.CacheStats();

            case "app.checkUpdate":
                return await UpdateChecker.CheckAsync(_http, Settings.UpdateRepo, Version, CancellationToken.None);

            case "geo.self":
                await RefreshSelfAsync(Arg<bool>(p, "force"));
                return OriginInfo();

            case "geo.cities":
                return HostnameHints.KnownCities().Select(c => new { city = c.City, cc = c.Cc, lat = c.Lat, lon = c.Lon })
                    .OrderBy(c => c.city, StringComparer.CurrentCulture).ToList();

            case "host.get":
            {
                var ip = Arg<string>(p, "ip");
                return ip == null ? null : _hosts.Get(ip);
            }

            case "connections.subscribe":
                _connections.Subscribe(Arg<bool>(p, "on"));
                return null;

            case "export.build":
                return BuildExport(Arg<string>(p, "format") ?? "txt");

            case "history.targets":
                return _history?.Targets() ?? [];

            case "history.runs":
                return _history?.Runs(Arg<string>(p, "target") ?? "") ?? [];

            case "history.run":
                return _history?.Run(Arg<long>(p, "id"));

            case "history.delete":
            {
                if (_history == null) return null;
                if (Arg<long?>(p, "id") is { } id) _history.DeleteRun(id);
                else if (Arg<string>(p, "target") is { } t) _history.DeleteTarget(t);
                return null;
            }

            default:
                throw new CommandException("unknown-command", Loc.S($"Неизвестная команда {type}", $"Unknown command {type}"));
        }
    }

    // ---------------- origin (your physical location) ----------------

    private string? TunnelAdapterId()
    {
        var adapters = AdapterService.List();
        var selected = Settings.AdapterId == null ? adapters.FirstOrDefault(a => a.IsWindowsChoice) : adapters.FirstOrDefault(a => a.Id == Settings.AdapterId);
        return selected?.Kind == "vpn" ? selected.Id : null;
    }

    private sealed record PhysicalInfo(IPAddress? Source, string? Name, string? AdapterId, bool TunnelUp, HashSet<string> TunnelServers);

    /// <summary>
    /// The physical adapter whose public IP represents the user's location — never the VPN exit. Always
    /// bound explicitly when a tunnel exists, so the request cannot follow the tunnel's default route.
    /// </summary>
    private PhysicalInfo PhysicalSource()
    {
        var adapters = AdapterService.List();
        var tunnelUp = adapters.Any(a => a.Kind == "vpn" && a.Status == "up" && a.HasRoute);
        var selected = Settings.AdapterId == null ? adapters.FirstOrDefault(a => a.IsWindowsChoice) : adapters.FirstOrDefault(a => a.Id == Settings.AdapterId);
        AdapterInfo? physical = null;
        var servers = new HashSet<string>();
        foreach (var t in adapters.Where(a => a.Kind == "vpn" && a.Status == "up").Select(a => TunnelDetector.Detect(a.Id)).OfType<TunnelInfo>())
        {
            foreach (var sv in t.Servers) servers.Add(sv.Ip);
            if (selected?.Id == t.AdapterId && t.CarrierId != null) physical = adapters.FirstOrDefault(a => a.Id == t.CarrierId);
        }
        if (Settings.VpnServer != null) servers.Add(Settings.VpnServer);
        if (selected != null && selected.Kind != "vpn") physical = selected;
        physical ??= adapters.FirstOrDefault(a => a.Status == "up" && a.HasGateway && a.HasRoute && a.Kind is "ethernet" or "wifi" or "cellular");
        var ip = physical?.Ipv4.FirstOrDefault(x => !x.Address.StartsWith("169.254."))?.Address;
        // Without a tunnel the default route is physical anyway; with one, binding is mandatory.
        var bind = ip != null && (tunnelUp || Settings.AdapterId != null) ? IPAddress.Parse(ip) : null;
        return new PhysicalInfo(bind, physical?.Name, physical?.Id, tunnelUp, servers);
    }

    /// <summary>Settings combination the cached location was obtained for.</summary>
    private string SelfContext() => $"{Settings.AdapterId}|{Settings.VpnMode}";

    private void InvalidateSelf()
    {
        _self = null;
        _selfContext = null;
        _selfNote = null;
    }

    private async Task RefreshSelfAsync(bool force)
    {
        if (!_hosts.Online || !Settings.Providers.CloudflareSelf || Settings.Origin.Mode == "manual") return;
        var context = SelfContext();
        if (!force && _self != null && _selfContext == context && GeoCandidate.Now - _self.FetchedAt < 3_600_000) return;
        await _selfLock.WaitAsync();
        try
        {
            var phys = await Task.Run(PhysicalSource);
            var metaTask = CloudflareMetaClient.FetchAsync(phys.Source, CancellationToken.None);
            // With a tunnel up, also ask through the tunnel: if both answers show the same IP, the
            // "physical" request leaked into the VPN and its location is the VPN exit, not the user.
            var tunnelTask = phys.TunnelUp && phys.Source != null
                ? CloudflareMetaClient.FetchAsync(null, CancellationToken.None)
                : Task.FromResult<CloudflareMeta?>(null);
            var meta = await metaTask;
            var viaTunnel = await tunnelTask;

            // Never keep a value obtained for another adapter or mode.
            InvalidateSelf();
            _selfVia = phys.Name;
            if (meta == null)
            {
                _selfNote = Loc.S($"Cloudflare не ответил через «{phys.Name ?? "физический адаптер"}». Точка отсчёта взята по первому узлу провайдера; город можно указать вручную.",
                    $"Cloudflare did not answer via \"{phys.Name ?? "the physical adapter"}\". The starting point is taken from your provider's first router; you can set your city manually.");
            }
            else if (meta.ClientIp != null && phys.TunnelServers.Contains(meta.ClientIp))
            {
                _selfNote = Loc.S($"Запрос через «{phys.Name}» ушёл в VPN: Cloudflare видит адрес сервера VPN {meta.ClientIp} ({meta.City ?? meta.Country}). Это не ваше место, оно не используется. Точка отсчёта взята по первому узлу провайдера; город можно указать вручную.",
                    $"The request via \"{phys.Name}\" went into the VPN: Cloudflare sees the VPN server address {meta.ClientIp} ({meta.City ?? meta.Country}). That is not your location and is not used. The starting point is taken from your provider's first router; you can set your city manually.");
            }
            else if (viaTunnel?.ClientIp != null && viaTunnel.ClientIp == meta.ClientIp)
            {
                // Ambiguous: either the VPN sends Cloudflare direct (split routing) or the request leaked.
                // Keep it; the route solver checks it against the RTT to your provider's first routers.
                _self = meta;
                _selfContext = context;
                _selfNote = Loc.S($"Через VPN и напрямую Cloudflare видит один и тот же IP {meta.ClientIp}. Либо VPN пускает Cloudflare мимо туннеля, либо запрос ушёл в туннель. Место ({meta.City}) дополнительно проверяется по задержке до первых узлов провайдера.",
                    $"Cloudflare sees the same IP {meta.ClientIp} through the VPN and directly. Either the VPN routes Cloudflare around the tunnel or the request went into the tunnel. The location ({meta.City}) is also checked against the latency to your provider's first routers.");
            }
            else
            {
                _self = meta;
                _selfContext = context;
                if (meta.ClientIp != null && IPAddress.TryParse(meta.ClientIp, out var pub)) _hosts.Discover(pub, full: false);
            }
            _sink.Emit("geo.origin", OriginInfo());
        }
        finally
        {
            _selfLock.Release();
        }
    }

    private object OriginInfo() => new
    {
        mode = Settings.Origin.Mode,
        manual = Settings.Origin.Mode == "manual" ? Settings.Origin : null,
        publicIp = _self?.ClientIp,
        asn = _self?.Asn,
        asOrg = _self?.AsOrganization,
        city = _self?.City,
        region = _self?.Region,
        country = _self?.Country,
        lat = _self?.Latitude,
        lon = _self?.Longitude,
        colo = _self?.ColoIata,
        coloCity = _self?.ColoCity,
        via = _selfVia,
        source = _self != null ? "Cloudflare (speed.cloudflare.com/meta)" : null,
        fetchedAt = _self?.FetchedAt,
        note = _selfNote,
        point = CurrentOrigin(),
    };

    private GeoPoint? CurrentOrigin()
    {
        var o = Settings.Origin;
        if (o.Mode == "manual" && o.Lat is { } lat && o.Lon is { } lon) return new GeoPoint(lat, lon, o.Label ?? Loc.S("вручную", "manual"), "manual");
        // Only a value fetched for the current adapter/mode is valid.
        if (_selfContext == SelfContext() && _self?.Latitude is { } sl && _self.Longitude is { } sn) return new GeoPoint(sl, sn, _self.City, "cloudflare");
        return null;
    }

    // ---------------- sessions ----------------

    private async Task<string> StartSessionAsync(string target)
    {
        await _sessionLock.WaitAsync();
        try
        {
            await FinishRunAsync();
            if (_overlay != null)
            {
                await _overlay.DisposeAsync();
                _overlay = null;
            }
            if (_session != null)
            {
                var old = _session;
                _session = null;
                await old.DisposeAsync();
            }

            var id = $"s{Interlocked.Increment(ref _sessionCounter)}-{DateTime.Now:HHmmss}";
            var adapters = AdapterService.List();
            var selected = Settings.AdapterId == null ? adapters.FirstOrDefault(a => a.IsWindowsChoice) : adapters.FirstOrDefault(a => a.Id == Settings.AdapterId);
            var tunnelSelected = selected?.Kind == "vpn";

            MtrSession session;
            if (Settings.VpnMode && tunnelSelected)
            {
                var tunnel = TunnelDetector.Detect(selected!.Id) ?? throw new CommandException("vpn-no-tunnel", Loc.S("VPN-адаптер не найден.", "VPN adapter not found."));
                TunnelServerCandidate? server = null;
                if (Settings.VpnServer is { } manual && TargetParser.Parse(manual) is { } ms)
                    server = new TunnelServerCandidate(ms.Host, ms.Port ?? 0, 0);
                server ??= tunnel.Servers.FirstOrDefault();
                if (server == null)
                    throw new CommandException("vpn-no-server",
                        Loc.S($"Не удалось определить сервер VPN: у {(tunnel.Process ?? "VPN-клиента")} нет TCP-соединений через физический адаптер (вероятно, UDP-протокол). Укажите IP сервера вручную во вкладке «Сведения».",
                            $"Could not find the VPN server: {(tunnel.Process ?? "the VPN client")} has no TCP connections through the physical adapter (probably a UDP protocol). Enter the server IP manually on the Details tab."));
                if (tunnel.CarrierId == null)
                    throw new CommandException("vpn-no-carrier", Loc.S("Не найден физический адаптер, через который работает туннель.", "The physical adapter the tunnel runs over was not found."));
                session = new MtrSession(id, server.Ip, Settings.Options, tunnel.CarrierId, _resolver, _sink, SessionsDir, ip => _hosts.Discover(ip));
                _overlay = new VpnOverlay(session, target, tunnel, server, _hosts, Settings.Options);
            }
            else
            {
                session = new MtrSession(id, target, Settings.Options, Settings.AdapterId, _resolver, _sink, SessionsDir, ip => _hosts.Discover(ip))
                {
                    TunnelAdapter = tunnelSelected,
                };
            }
            session.HostLookup = _hosts.Get;
            session.OriginProvider = CurrentOrigin;
            session.DestinationReached = (ip, host, src) => _hosts.CheckCloudflareColo(ip, host, src);
            _session = session;

            var overlay = _overlay;
            _ = Task.Run(async () =>
            {
                try
                {
                    _ = RefreshSelfAsync(false);
                    await session.StartAsync();
                    if (session.State.State is "running")
                    {
                        overlay?.Start();
                        BeginRun(session, target, selected);
                    }
                }
                catch (Exception e)
                {
                    session.State.State = "error";
                    session.State.Error = e.Message;
                    _sink.Emit("trace.state", session.State);
                }
            });
            return id;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private static string TargetKey(string input) => (TargetParser.Parse(input)?.Host ?? input).ToLowerInvariant();

    private void BeginRun(MtrSession session, string target, AdapterInfo? adapter)
    {
        if (_history == null) return;
        try
        {
            var key = TargetKey(target);
            var previous = _history.LastRun(key);
            _runId = _history.StartRun(new RunStart(key, target, TargetParser.Parse(target)?.Host, session.State.OverlayIp ?? session.State.TargetIp,
                session.State.Mode, adapter?.Id, adapter?.Name ?? Loc.S("Авто", "Auto"), session.State.SourceIp, _self?.ClientIp,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Settings.Options));
            if (previous != null) _sink.Emit("history.previous", new { sessionId = session.Id, run = previous });
            var runId = _runId.Value;
            _historyTimer = new Timer(_ => WriteSnapshot(session, runId), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine(e);
        }
    }

    private void WriteSnapshot(MtrSession session, long runId)
    {
        if (_history == null || session.State.State is not ("running" or "paused" or "stopped" or "finished")) return;
        try
        {
            var snap = session.Snapshot();
            double? avg = null, loss = null;
            if (snap.Vpn is VpnBlockDto v)
            {
                avg = v.Avg;
                loss = v.Loss;
            }
            _history.AddSnapshot(runId, snap, ip => _hosts.Get(ip)?.Hostname, avg, loss);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine(e);
        }
    }

    private async Task FinishRunAsync()
    {
        _historyTimer?.Dispose();
        _historyTimer = null;
        if (_runId is { } id && _session != null && _history != null)
        {
            await Task.Run(() => WriteSnapshot(_session, id));
            _history.EndRun(id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _sink.Emit("history.changed", new { target = TargetKey(_session.State.OverlayInput ?? _session.State.Input) });
        }
        _runId = null;
    }

    private object BuildExport(string format)
    {
        var s = _session ?? throw new CommandException("no-session", Loc.S("Нет данных для экспорта.", "Nothing to export."));
        if (s.State.TargetIp == null) throw new CommandException("no-session", Loc.S("Трассировка ещё не началась.", "The trace has not started yet."));
        string? adapterName = null;
        if (s.State.AdapterId != null) adapterName = AdapterService.Find(s.State.AdapterId)?.Name;
        var snap = s.Snapshot();
        var ctx = new ExportContext(s.State, snap, s.Options, s.Log.Rows(), s.Log.Events(), _hosts.Get, adapterName, s.SolveGeo(snap));
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var baseName = $"mtr-{Sanitize(s.State.OverlayInput ?? s.State.Host ?? "trace")}-{stamp}";
        return format switch
        {
            "csv" => new { content = ReportExporter.ToCsv(ctx), fileName = baseName + ".csv", filter = "CSV|*.csv" },
            "json" => new { content = ReportExporter.ToJson(ctx), fileName = baseName + ".json", filter = "JSON|*.json" },
            "html" => new { content = ReportExporter.ToHtml(ctx), fileName = baseName + ".html", filter = "HTML|*.html" },
            _ => new { content = ReportExporter.ToText(ctx), fileName = baseName + ".txt", filter = "Text|*.txt" },
        };
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));

    private void EmitTraffic()
    {
        try
        {
            var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var items = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Select(n =>
                {
                    try
                    {
                        var st = n.GetIPStatistics();
                        return new { id = n.Id, sent = st.BytesSent, recv = st.BytesReceived };
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(x => x != null).ToList();
            _sink.Emit("adapters.traffic", new { t, items });
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _trafficTimer?.Dispose();
        _connections.Dispose();
        await FinishRunAsync();
        if (_overlay != null) await _overlay.DisposeAsync();
        if (_session != null) await _session.DisposeAsync();
        _db.Dispose();
        _history?.Dispose();
    }
}
