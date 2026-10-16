using System.IO.Compression;
using System.Net;
using MaxMind.Db;

namespace MTRTracer.Core.Geo;

/// <summary>
/// Offline geolocation from the free DB-IP Lite databases (CC BY 4.0, monthly, no key):
/// dbip-city-lite for coordinates and dbip-asn-lite for AS numbers. Downloaded once, refreshed monthly.
/// </summary>
public sealed class DbIpDatabase : IDisposable
{
    private const string BaseUrl = "https://download.db-ip.com/free/";
    private readonly object _lock = new();
    private Reader? _city;
    private Reader? _asn;

    public string Directory { get; }
    public string Status { get; private set; } = "missing";
    public double Progress { get; private set; }
    public string? Stage { get; private set; }
    public string? DbDate { get; private set; }
    public string? Error { get; private set; }
    public bool Ready => _city != null;

    public event Action? Changed;

    public DbIpDatabase(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        LoadExisting();
    }

    private void LoadExisting()
    {
        var city = Latest("dbip-city-lite-");
        var asn = Latest("dbip-asn-lite-");
        lock (_lock)
        {
            try
            {
                if (city != null)
                {
                    _city?.Dispose();
                    _city = new Reader(city, FileAccessMode.MemoryMapped);
                    DbDate = Path.GetFileNameWithoutExtension(city)["dbip-city-lite-".Length..];
                    Status = "ready";
                }
                if (asn != null)
                {
                    _asn?.Dispose();
                    _asn = new Reader(asn, FileAccessMode.MemoryMapped);
                }
            }
            catch (Exception e)
            {
                Status = "error";
                Error = e.Message;
            }
        }
    }

    private string? Latest(string prefix) =>
        System.IO.Directory.GetFiles(Directory, prefix + "*.mmdb").OrderByDescending(f => f).FirstOrDefault();

    public bool NeedsUpdate()
    {
        if (_city == null) return true;
        // DB-IP publishes a new release every month; check for it on every launch.
        return DbDate != DateTime.UtcNow.ToString("yyyy-MM");
    }

    public async Task EnsureAsync(HttpClient http, CancellationToken ct)
    {
        if (!NeedsUpdate() || Status == "downloading") return;
        var hadDb = _city != null;
        Status = hadDb ? "updating" : "downloading";
        Error = null;
        Changed?.Invoke();
        try
        {
            var asnPath = await DownloadAsync(http, "dbip-asn-lite", 0.0, 0.08, ct);
            var cityPath = await DownloadAsync(http, "dbip-city-lite", 0.08, 1.0, ct);
            Stage = null;
            LoadExisting();
            Cleanup("dbip-city-lite-", cityPath);
            Cleanup("dbip-asn-lite-", asnPath);
            Status = "ready";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            Status = hadDb ? "ready" : "error";
            Error = e is TaskCanceledException ? "таймаут загрузки" : e.Message;
        }
        Changed?.Invoke();
    }

    private async Task<string> DownloadAsync(HttpClient http, string name, double from, double to, CancellationToken ct)
    {
        Stage = name;
        // The current month's file appears a few days into the month; fall back to earlier releases.
        var now = DateTime.UtcNow;
        Exception? last = null;
        for (var back = 0; back < 3; back++)
        {
            var month = now.AddMonths(-back).ToString("yyyy-MM");
            var target = Path.Combine(Directory, $"{name}-{month}.mmdb");
            if (File.Exists(target)) return target;
            try
            {
                using var resp = await http.GetAsync($"{BaseUrl}{name}-{month}.mmdb.gz", HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound) continue;
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                var tmp = target + ".download";
                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                await using (var counting = new ProgressStream(src, read =>
                {
                    if (total <= 0) return;
                    Progress = from + (to - from) * Math.Min(1, (double)read / total);
                    Changed?.Invoke();
                }))
                await using (var gz = new GZipStream(counting, CompressionMode.Decompress))
                await using (var dst = File.Create(tmp))
                {
                    await gz.CopyToAsync(dst, 1 << 16, ct);
                }
                File.Move(tmp, target, true);
                return target;
            }
            catch (HttpRequestException e)
            {
                last = e;
            }
        }
        throw last ?? new HttpRequestException($"{name}: файл не найден на сервере DB-IP");
    }

    private void Cleanup(string prefix, string keep)
    {
        foreach (var f in System.IO.Directory.GetFiles(Directory, prefix + "*"))
        {
            if (string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(f); } catch { }
        }
    }

    public GeoDto? LookupCity(IPAddress ip)
    {
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
        Reader? r;
        lock (_lock) r = _asn;
        if (r == null) return null;
        try
        {
            var d = r.Find<Dictionary<string, object>>(ip);
            if (d == null || !d.TryGetValue("autonomous_system_number", out var n)) return null;
            d.TryGetValue("autonomous_system_organization", out var org);
            return (Convert.ToInt32(n), org?.ToString());
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
        if (names.TryGetValue("ru", out var ru)) return ru?.ToString();
        if (names.TryGetValue("en", out var en)) return en?.ToString();
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
