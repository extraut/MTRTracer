using System.Text.Json;
using System.Text.RegularExpressions;

namespace MTRTracer.Core.Updates;

public sealed record UpdateAsset(string Name, string Url, long Size);

public sealed record UpdateInfo(
    string Repo,
    string Current,
    string? Latest,
    bool NewerAvailable,
    string? Url,
    string? PublishedAt,
    string? Notes,
    List<UpdateAsset> Assets,
    bool Archived,
    string? Error);

/// <summary>
/// Asks GitHub for the newest release of the configured repository (the original one by default, or a
/// fork the user points the app at). Runs only when the user presses "Check for updates".
/// </summary>
public static partial class UpdateChecker
{
    public const string DefaultRepo = "extraut/MTRTracer";

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoRx();

    public static string NormalizeRepo(string? repo)
    {
        var r = (repo ?? "").Trim().Trim('/');
        if (r.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) r = r["https://github.com/".Length..].Trim('/');
        if (r.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) r = r[..^4];
        var parts = r.Split('/');
        if (parts.Length > 2) r = parts[0] + "/" + parts[1];
        return RepoRx().IsMatch(r) ? r : DefaultRepo;
    }

    public static async Task<UpdateInfo> CheckAsync(HttpClient http, string repo, string current, CancellationToken ct)
    {
        repo = NormalizeRepo(repo);
        try
        {
            var archived = false;
            using (var req = Get($"https://api.github.com/repos/{repo}"))
            using (var resp = await http.SendAsync(req, ct))
            {
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    archived = doc.RootElement.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True;
                }
                else if ((int)resp.StatusCode == 404)
                {
                    return new UpdateInfo(repo, current, null, false, null, null, null, [], false,
                        Loc.S($"Репозиторий {repo} не найден.", $"Repository {repo} not found."));
                }
            }

            using var rreq = Get($"https://api.github.com/repos/{repo}/releases/latest");
            using var rresp = await http.SendAsync(rreq, ct);
            if ((int)rresp.StatusCode == 404)
                return new UpdateInfo(repo, current, null, false, $"https://github.com/{repo}/releases", null, null, [], archived,
                    Loc.S("В репозитории пока нет релизов.", "The repository has no releases yet."));
            if ((int)rresp.StatusCode is 403 or 429)
                return new UpdateInfo(repo, current, null, false, $"https://github.com/{repo}/releases", null, null, [], archived,
                    Loc.S("GitHub временно ограничил запросы, попробуйте позже.", "GitHub rate limit reached, try again later."));
            rresp.EnsureSuccessStatusCode();
            using var rdoc = JsonDocument.Parse(await rresp.Content.ReadAsStringAsync(ct));
            var root = rdoc.RootElement;
            var tag = Str(root, "tag_name");
            var assets = new List<UpdateAsset>();
            if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var x in arr.EnumerateArray())
                    if (Str(x, "name") is { } n && Str(x, "browser_download_url") is { } u)
                        assets.Add(new UpdateAsset(n, u, x.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var l) ? l : 0));
            var notes = Str(root, "body");
            if (notes is { Length: > 4000 }) notes = notes[..4000] + "…";
            return new UpdateInfo(repo, current, tag, IsNewer(tag, current), Str(root, "html_url"), Str(root, "published_at"), notes, assets, archived, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UpdateInfo(repo, current, null, false, null, null, null, [], false,
                e is TaskCanceledException ? Loc.S("GitHub не ответил вовремя.", "GitHub did not respond in time.") : e.Message);
        }
    }

    private static HttpRequestMessage Get(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Compares "v1.2.3"-style tags numerically; anything unparseable is not newer.</summary>
    public static bool IsNewer(string? tag, string current)
    {
        static int[]? Parse(string? s)
        {
            if (s == null) return null;
            var m = Regex.Match(s, @"(\d+)\.(\d+)(?:\.(\d+))?");
            return m.Success ? [int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0] : null;
        }
        var a = Parse(tag);
        var b = Parse(current);
        if (a == null || b == null) return false;
        for (var i = 0; i < 3; i++)
            if (a[i] != b[i]) return a[i] > b[i];
        return false;
    }
}
