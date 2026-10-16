using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using MaxMind.Db;

namespace MTRTracer.Core.Geo;

/// <summary>How the offline database is chosen and kept up to date (stored in settings.json).</summary>
public sealed class GeoDbSetting
{
    /// <summary>Check for a new DB-IP release at launch and download it.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Active database: a DB-IP release "yyyy-MM", "custom" for an imported file, null for the newest installed.</summary>
    public string? Active { get; set; }

    /// <summary>Where DB-IP releases are downloaded from; {name} and {month} are substituted. Editable in case the address changes.</summary>
    public string UrlTemplate { get; set; } = DefaultUrlTemplate;

    public const string DefaultUrlTemplate = "https://download.db-ip.com/free/{name}-{month}.mmdb.gz";

    public GeoDbSetting Normalize()
    {
        if (string.IsNullOrWhiteSpace(UrlTemplate) || !UrlTemplate.Contains("{month}") || !UrlTemplate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            UrlTemplate = DefaultUrlTemplate;
        if (Active is not null && Active != "custom" && !Regex.IsMatch(Active, @"^\d{4}-\d{2}$")) Active = null;
        return this;
    }
}

public sealed record GeoDbRelease(string Id, string Label, bool City, bool Asn, long Bytes, string? Type, string? Built, bool Active);

public sealed record GeoDbAvailability(string Month, bool Available, long? Bytes, bool Installed);

/// <summary>
/// Offline geolocation from MaxMind-format databases. By default the free DB-IP Lite releases (CC BY 4.0,
/// monthly, no key): dbip-city-lite for coordinates and dbip-asn-lite for AS numbers. Several releases can be
/// kept side by side and one of them chosen; a MaxMind GeoLite2 / DB-IP file can also be imported by hand.
/// </summary>
public sealed class DbIpDatabase : IDisposable
{
    private const string CityPrefix = "dbip-city-lite-";
    private const string AsnPrefix = "dbip-asn-lite-";
    private const string CustomCity = "custom-city.mmdb";
    private const string CustomAsn = "custom-asn.mmdb";

    private readonly object _lock = new();
    private Reader? _city;
    private Reader? _asn;
    private int _busy;

    public string Directory { get; }
    public GeoDbSetting Setting { get; set; } = new();
    public string Status { get; private set; } = "missing";
    public double Progress { get; private set; }
    public string? Stage { get; private set; }
    /// <summary>Label of the active city database: "2026-10" or the imported file's type.</summary>
    public string? DbDate { get; private set; }
    public string? ActiveId { get; private set; }
    public string? Error { get; private set; }
    public bool Ready => _city != null;
    public bool Enabled { get; set; } = true;

    public event Action? Changed;

    public DbIpDatabase(string directory, GeoDbSetting? setting = null)
    {
        Directory = directory;
        if (setting != null) Setting = setting;
        System.IO.Directory.CreateDirectory(directory);
        Load();
    }

    // ---------------- which files are active ----------------

    private string P(string file) => Path.Combine(Directory, file);

    private IEnumerable<string> Months(string prefix) =>
        System.IO.Directory.GetFiles(Directory, prefix + "*.mmdb")
            .Select(f => Path.GetFileNameWithoutExtension(f)[prefix.Length..])
            .Where(m => Regex.IsMatch(m, @"^\d{4}-\d{2}$"))
            .OrderByDescending(m => m, StringComparer.Ordinal);

    private (string? City, string? Asn, string? Id) Resolve()
    {
        var newestCity = Months(CityPrefix).FirstOrDefault();
        var newestAsn = Months(AsnPrefix).FirstOrDefault();
        string? FileOr(string prefix, string? month) => month != null && File.Exists(P($"{prefix}{month}.mmdb")) ? P($"{prefix}{month}.mmdb") : null;

        if (Setting.Active == "custom" && (File.Exists(P(CustomCity)) || File.Exists(P(CustomAsn))))
            return (File.Exists(P(CustomCity)) ? P(CustomCity) : FileOr(CityPrefix, newestCity),
                File.Exists(P(CustomAsn)) ? P(CustomAsn) : FileOr(AsnPrefix, newestAsn), "custom");
        if (Setting.Active is { } m && m != "custom" && File.Exists(P($"{CityPrefix}{m}.mmdb")))
            return (P($"{CityPrefix}{m}.mmdb"), FileOr(AsnPrefix, m) ?? FileOr(AsnPrefix, newestAsn), m);
        return (FileOr(CityPrefix, newestCity), FileOr(AsnPrefix, newestAsn), newestCity);
    }

    /// <summary>(Re)opens the files that the current setting points at.</summary>
    public void Load()
    {
        var (city, asn, id) = Resolve();
        lock (_lock)
        {
            try
            {
                _city?.Dispose();
                _asn?.Dispose();
                _city = city != null ? new Reader(city, FileAccessMode.MemoryMapped) : null;
                _asn = asn != null ? new Reader(asn, FileAccessMode.MemoryMapped) : null;
                ActiveId = id;
                DbDate = id == "custom" ? _city?.Metadata.DatabaseType ?? Loc.S("свой файл", "custom file") : id;
                if (Status is not ("downloading" or "updating")) Status = _city != null ? "ready" : "missing";
            }
            catch (Exception e)
            {
                _city = null;
                Status = "error";
                Error = e.Message;
            }
        }
    }

    public List<GeoDbRelease> Releases()
    {
        var list = new List<GeoDbRelease>();
        var months = Months(CityPrefix).Union(Months(AsnPrefix)).OrderByDescending(m => m, StringComparer.Ordinal);
        foreach (var m in months)
        {
            var c = new FileInfo(P($"{CityPrefix}{m}.mmdb"));
            var a = new FileInfo(P($"{AsnPrefix}{m}.mmdb"));
            list.Add(new GeoDbRelease(m, $"DB-IP Lite {m}", c.Exists, a.Exists, (c.Exists ? c.Length : 0) + (a.Exists ? a.Length : 0),
                "DB-IP Lite", m, ActiveId == m));
        }
        var cc = new FileInfo(P(CustomCity));
        var ca = new FileInfo(P(CustomAsn));
        if (cc.Exists || ca.Exists)
        {
            var (type, built) = Describe(cc.Exists ? cc.FullName : ca.FullName);
            if (cc.Exists && ca.Exists) type = $"{type} + {Describe(ca.FullName).Type}";
            list.Add(new GeoDbRelease("custom", Loc.S("Свой файл", "Imported file"), cc.Exists, ca.Exists,
                (cc.Exists ? cc.Length : 0) + (ca.Exists ? ca.Length : 0), type, built, ActiveId == "custom"));
        }
        return list;
    }

    private static (string? Type, string? Built) Describe(string file)
    {
        try
        {
            using var r = new Reader(file, FileAccessMode.Memory);
            return (r.Metadata.DatabaseType, r.Metadata.BuildDate.ToString("yyyy-MM-dd"));
        }
        catch
        {
            return (null, null);
        }
    }

    public void Use(string? id)
    {
        Setting.Active = id;
        Setting.Normalize();
        Load();
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            _city?.Dispose();
            _asn?.Dispose();
            _city = _asn = null;
        }
        var files = id == "custom" ? new[] { P(CustomCity), P(CustomAsn) } : new[] { P($"{CityPrefix}{id}.mmdb"), P($"{AsnPrefix}{id}.mmdb") };
        foreach (var f in files)
            try { File.Delete(f); } catch { }
        if (Setting.Active == id) Setting.Active = null;
        Status = "missing";
        Load();
        Changed?.Invoke();
    }

    // ---------------- downloads ----------------

    public bool NeedsUpdate()
    {
        if (_city == null) return true;
        // Pinned to a release or a custom file: the user decides when to change it.
        if (Setting.Active != null || !Setting.AutoUpdate) return false;
        // DB-IP publishes a new release every month; check for it on every launch.
        return DbDate != DateTime.UtcNow.ToString("yyyy-MM");
    }

    /// <summary>Launch-time check: downloads the newest release when there is no database or auto-update is on.</summary>
    public Task EnsureAsync(HttpClient http, CancellationToken ct) =>
        Enabled && NeedsUpdate() ? DownloadReleaseAsync(http, null, prune: true, ct) : Task.CompletedTask;

    private string Url(string name, string month) => Setting.UrlTemplate.Replace("{name}", name).Replace("{month}", month);

    /// <summary>Which of the last <paramref name="count"/> monthly releases the server has (HEAD requests only).</summary>
    public async Task<List<GeoDbAvailability>> AvailableAsync(HttpClient http, int count, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var months = Enumerable.Range(0, count).Select(i => now.AddMonths(-i).ToString("yyyy-MM")).ToList();
        var installed = Months(CityPrefix).ToHashSet();
        var tasks = months.Select(async m =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, Url("dbip-city-lite", m));
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                return new GeoDbAvailability(m, resp.IsSuccessStatusCode, resp.Content.Headers.ContentLength, installed.Contains(m));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                return new GeoDbAvailability(m, false, null, installed.Contains(m));
            }
        });
        return [.. await Task.WhenAll(tasks)];
    }

    /// <summary>
    /// Downloads a DB-IP release (city + ASN). <paramref name="month"/> null = the newest one the server has
    /// (the current month's file appears a few days into the month, so earlier ones are tried too).
    /// </summary>
    public async Task DownloadReleaseAsync(HttpClient http, string? month, bool prune, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        var hadDb = _city != null;
        Status = hadDb ? "updating" : "downloading";
        Error = null;
        Progress = 0;
        Changed?.Invoke();
        try
        {
            List<string> months = month != null
                ? [month]
                : Enumerable.Range(0, 3).Select(i => DateTime.UtcNow.AddMonths(-i).ToString("yyyy-MM")).ToList();
            var asnPath = await DownloadAsync(http, "dbip-asn-lite", months, 0.0, 0.08, ct);
            var got = Path.GetFileNameWithoutExtension(asnPath)[AsnPrefix.Length..];
            var cityPath = await DownloadAsync(http, "dbip-city-lite", [got, .. months.Where(m => m != got)], 0.08, 1.0, ct);
            Stage = null;
            if (prune && Setting.Active == null)
            {
                Cleanup(CityPrefix, cityPath);
                Cleanup(AsnPrefix, asnPath);
            }
            Status = "ready";
            Load();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            Stage = null;
            Status = hadDb ? "ready" : "error";
            Error = e is TaskCanceledException ? Loc.S("таймаут загрузки", "download timed out") : e.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
        Changed?.Invoke();
    }

    private async Task<string> DownloadAsync(HttpClient http, string name, IReadOnlyList<string> months, double from, double to, CancellationToken ct)
    {
        Stage = name;
        Exception? last = null;
        foreach (var month in months)
        {
            var target = P($"{name}-{month}.mmdb");
            if (File.Exists(target)) return target;
            try
            {
                using var resp = await http.GetAsync(Url(name, month), HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound) continue;
                resp.EnsureSuccessStatusCode();
                await SaveAsync(resp, target, gzip: true, from, to, ct);
                return target;
            }
            catch (HttpRequestException e)
            {
                last = e;
            }
        }
        throw last ?? new HttpRequestException(Loc.S($"{name}: выпуск {string.Join(", ", months)} не найден на сервере", $"{name}: release {string.Join(", ", months)} not found on the server"));
    }

    private async Task SaveAsync(HttpResponseMessage resp, string target, bool gzip, double from, double to, CancellationToken ct)
    {
        var total = resp.Content.Headers.ContentLength ?? 0;
        var tmp = target + ".download";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var counting = new ProgressStream(src, read =>
        {
            if (total <= 0) return;
            Progress = from + (to - from) * Math.Min(1, (double)read / total);
            Changed?.Invoke();
        }))
        await using (var dst = File.Create(tmp))
        {
            if (gzip)
            {
                await using var gz = new GZipStream(counting, CompressionMode.Decompress);
                await gz.CopyToAsync(dst, 1 << 16, ct);
            }
            else
            {
                await counting.CopyToAsync(dst, 1 << 16, ct);
            }
        }
        File.Move(tmp, target, true);
    }

    /// <summary>
    /// Downloads any MaxMind-format database by URL (.mmdb, .mmdb.gz or a .tar.gz with an .mmdb inside, as
    /// MaxMind ships GeoLite2) and imports it as the custom database.
    /// </summary>
    public async Task<string> FetchUrlAsync(HttpClient http, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https")
            throw new CommandException("bad-url", Loc.S("Нужна ссылка https://…", "An https://… link is required"));
        if (Interlocked.Exchange(ref _busy, 1) == 1) throw new CommandException("busy", Loc.S("Уже идёт загрузка.", "A download is already running."));
        var tmp = P("fetch-" + Guid.NewGuid().ToString("N") + Path.GetExtension(u.AbsolutePath));
        try
        {
            Status = _city != null ? "updating" : "downloading";
            Stage = u.Host;
            Error = null;
            Changed?.Invoke();
            using var resp = await http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await SaveAsync(resp, tmp, gzip: false, 0, 1, ct);
            Stage = null;
            Status = _city != null ? "ready" : "missing";
            return ImportCore(tmp, Path.GetFileName(u.AbsolutePath));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            Stage = null;
            Status = _city != null ? "ready" : "error";
            Error = e is TaskCanceledException ? Loc.S("таймаут загрузки", "download timed out") : e.Message;
            Changed?.Invoke();
            throw new CommandException("download", Error);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Imports a local .mmdb / .mmdb.gz / .tar.gz as the custom city or ASN database (detected from its metadata).</summary>
    public string Import(string path)
    {
        if (!File.Exists(path)) throw new CommandException("not-found", Loc.S("Файл не найден.", "File not found."));
        return ImportCore(path, Path.GetFileName(path));
    }

    private string ImportCore(string path, string name)
    {
        var mmdb = P("import-" + Guid.NewGuid().ToString("N") + ".mmdb");
        try
        {
            var lower = name.ToLowerInvariant();
            if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz")) ExtractTar(path, mmdb, gz: true);
            else if (lower.EndsWith(".tar")) ExtractTar(path, mmdb, gz: false);
            else if (lower.EndsWith(".gz"))
            {
                using var src = File.OpenRead(path);
                using var gz = new GZipStream(src, CompressionMode.Decompress);
                using var dst = File.Create(mmdb);
                gz.CopyTo(dst);
            }
            else File.Copy(path, mmdb, true);

            string type;
            bool hasLocation;
            try
            {
                using var r = new Reader(mmdb, FileAccessMode.Memory);
                type = r.Metadata.DatabaseType ?? "";
                hasLocation = !type.Contains("ASN", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e)
            {
                throw new CommandException("bad-file", Loc.S($"Это не база MaxMind (.mmdb): {e.Message}", $"Not a MaxMind database (.mmdb): {e.Message}"));
            }
            lock (_lock)
            {
                _city?.Dispose();
                _asn?.Dispose();
                _city = _asn = null;
            }
            File.Move(mmdb, P(hasLocation ? CustomCity : CustomAsn), true);
            Setting.Active = "custom";
            Load();
            Changed?.Invoke();
            return hasLocation ? "city" : "asn";
        }
        finally
        {
            try { File.Delete(mmdb); } catch { }
        }
    }

    private static void ExtractTar(string path, string target, bool gz)
    {
        using var src = File.OpenRead(path);
        using Stream data = gz ? new GZipStream(src, CompressionMode.Decompress) : src;
        using var tar = new TarReader(data);
        while (tar.GetNextEntry() is { } e)
        {
            if (e.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && e.Name.EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase) && e.DataStream != null)
            {
                using var dst = File.Create(target);
                e.DataStream.CopyTo(dst);
                return;
            }
        }
        throw new InvalidDataException(Loc.S("В архиве нет файла .mmdb", "The archive has no .mmdb file"));
    }

    private void Cleanup(string prefix, string keep)
    {
        foreach (var f in System.IO.Directory.GetFiles(Directory, prefix + "*"))
        {
            if (string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(f); } catch { }
        }
    }

    // ---------------- lookups ----------------

    public GeoDto? LookupCity(IPAddress ip)
    {
        if (!Enabled) return null;
        Reader? r;
        lock (_lock) r = _city;
        if (r == null) return null;
        try
        {
            var d = r.Find<Dictionary<string, object>>(ip);
            if (d == null || !d.TryGetValue("location", out var locObj) || locObj is not Dictionary<string, object> loc) return null;
            var lat = Convert.ToDouble(loc["latitude"]);
            var lon = Convert.ToDouble(loc["longitude"]);
            var city = Name(d, "city");
            string? region = null;
            if (d.TryGetValue("subdivisions", out var subs) && subs is List<object> { Count: > 0 } list && list[0] is Dictionary<string, object> sub)
                region = Names(sub);
            string? cc = null;
            if (d.TryGetValue("country", out var cObj) && cObj is Dictionary<string, object> c && c.TryGetValue("iso_code", out var iso))
                cc = iso?.ToString();
            return new GeoDto(lat, lon, city, region, Name(d, "country"), cc, "dbip", city != null ? "city" : "country");
        }
        catch
        {
            return null;
        }
    }

    public (int Asn, string? Org)? LookupAsn(IPAddress ip)
    {
        if (!Enabled) return null;
        Reader? r;
        lock (_lock) r = _asn;
        if (r == null) return null;
        try
        {
            var d = r.Find<Dictionary<string, object>>(ip);
            if (d == null) return null;
            if (d.TryGetValue("autonomous_system_number", out var n))
            {
                d.TryGetValue("autonomous_system_organization", out var org);
                return (Convert.ToInt32(n), org?.ToString());
            }
            // IPinfo-style ASN databases: "asn": "AS13335", "as_name": "...".
            if (d.TryGetValue("asn", out var a) && a?.ToString() is { } s && int.TryParse(s.StartsWith("AS", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, out var num))
            {
                d.TryGetValue("as_name", out var name);
                return (num, name?.ToString());
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? Name(Dictionary<string, object> d, string key) =>
        d.TryGetValue(key, out var o) && o is Dictionary<string, object> m ? Names(m) : null;

    private static string? Names(Dictionary<string, object> m)
    {
        if (!m.TryGetValue("names", out var n) || n is not Dictionary<string, object> names) return null;
        var first = Loc.En ? "en" : "ru";
        var second = Loc.En ? "ru" : "en";
        if (names.TryGetValue(first, out var a)) return a?.ToString();
        if (names.TryGetValue(second, out var b)) return b?.ToString();
        return names.Values.FirstOrDefault()?.ToString();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _city?.Dispose();
            _asn?.Dispose();
            _city = null;
            _asn = null;
        }
    }

    private sealed class ProgressStream(Stream inner, Action<long> report) : Stream
    {
        private long _read;
        private long _lastReport;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Track(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Track(await inner.ReadAsync(buffer, ct));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Track(int n)
        {
            _read += n;
            if (_read - _lastReport > 256 * 1024 || n == 0)
            {
                _lastReport = _read;
                report(_read);
            }
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
