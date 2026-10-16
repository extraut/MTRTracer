using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MTRTracer.Core.Logging;
using MTRTracer.Core.Protocol;

namespace MTRTracer.Core.History;

public sealed record RunStart(
    string TargetKey,
    string Input,
    string? Host,
    string? TargetIp,
    string Mode,
    string? AdapterId,
    string? AdapterName,
    string? SourceIp,
    string? PublicIp,
    long StartedAt,
    TraceOptions Options);

public sealed record HistoryHop(int Ttl, string? Ip, string? Host, double Loss, int Sent, double? Avg, double? Best, double? Worst, double? JAvg);

public sealed record SnapshotRow(long T, double? Loss, double? Avg, double? Best, double? Worst, double? Jitter, string RouteHash, int Rounds);

public sealed record RunSummary(
    long Id,
    string TargetKey,
    string Input,
    string? TargetIp,
    string Mode,
    string? AdapterName,
    string? SourceIp,
    string? PublicIp,
    long StartedAt,
    long? EndedAt,
    int Snapshots,
    double? Loss,
    double? Avg,
    double? Best,
    double? Jitter,
    int Hops,
    string? RouteHash);

public sealed record TargetSummary(string TargetKey, int Runs, long LastAt, double? LastAvg, double? LastLoss, List<double?> AvgTrend);

public sealed record RunDetail(RunSummary Run, List<SnapshotRow> Snapshots, List<HistoryHop> Hops, JsonElement? Options);

/// <summary>
/// Long-term journal of measurements (SQLite). Every session becomes a run; a snapshot of the destination
/// metrics and the full hop table is written every minute and at the end, so runs from the afternoon and
/// the evening can be compared hop by hop.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public HistoryStore(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        _db.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS runs(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              target_key TEXT NOT NULL, input TEXT NOT NULL, host TEXT, target_ip TEXT, mode TEXT NOT NULL,
              adapter_id TEXT, adapter_name TEXT, source_ip TEXT, public_ip TEXT,
              started_at INTEGER NOT NULL, ended_at INTEGER, options TEXT);
            CREATE INDEX IF NOT EXISTS ix_runs_target ON runs(target_key, started_at);
            CREATE TABLE IF NOT EXISTS snapshots(
              run_id INTEGER NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
              t INTEGER NOT NULL, rounds INTEGER, loss REAL, avg REAL, best REAL, worst REAL, jitter REAL,
              route_hash TEXT, hops TEXT);
            CREATE INDEX IF NOT EXISTS ix_snap_run ON snapshots(run_id, t);
            PRAGMA foreign_keys=ON;
            """);
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Cmd(string sql, params (string, object?)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    public long StartRun(RunStart r)
    {
        lock (_lock)
        {
            using var cmd = Cmd("""
                INSERT INTO runs(target_key,input,host,target_ip,mode,adapter_id,adapter_name,source_ip,public_ip,started_at,options)
                VALUES($k,$i,$h,$ip,$m,$aid,$an,$src,$pub,$t,$o); SELECT last_insert_rowid();
                """,
                ("$k", r.TargetKey), ("$i", r.Input), ("$h", r.Host), ("$ip", r.TargetIp), ("$m", r.Mode), ("$aid", r.AdapterId),
                ("$an", r.AdapterName), ("$src", r.SourceIp), ("$pub", r.PublicIp), ("$t", r.StartedAt),
                ("$o", JsonSerializer.Serialize(r.Options, Json.Options)));
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public static string RouteHash(IEnumerable<string?> primaries)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(string.Join(">", primaries.Select(p => p ?? "*"))));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }

    public void AddSnapshot(long runId, HopsSnapshotDto snap, Func<string, string?> hostname, double? overrideAvg = null, double? overrideLoss = null)
    {
        if (snap.Hops.Count == 0) return;
        var dest = snap.Hops.FirstOrDefault(h => h.IsDestination) ?? snap.Hops.LastOrDefault(h => h.Recv > 0);
        var hops = snap.Hops.Select(h => new HistoryHop(h.Ttl, h.Primary, h.Primary == null ? null : hostname(h.Primary), h.Loss, h.Sent, h.Avg, h.Best, h.Worst, h.JAvg)).ToList();
        lock (_lock)
        {
            using var cmd = Cmd("""
                INSERT INTO snapshots(run_id,t,rounds,loss,avg,best,worst,jitter,route_hash,hops)
                VALUES($r,$t,$n,$l,$a,$b,$w,$j,$h,$hops)
                """,
                ("$r", runId), ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$n", snap.Round),
                ("$l", overrideLoss ?? dest?.Loss), ("$a", overrideAvg ?? dest?.Avg), ("$b", dest?.Best), ("$w", dest?.Worst), ("$j", dest?.JAvg),
                ("$h", RouteHash(snap.Hops.Select(h => h.Primary))), ("$hops", JsonSerializer.Serialize(hops, Json.Options)));
            cmd.ExecuteNonQuery();
        }
    }

    public void EndRun(long runId, long endedAt)
    {
        lock (_lock)
        {
            using var cmd = Cmd("UPDATE runs SET ended_at=$e WHERE id=$id", ("$e", endedAt), ("$id", runId));
            cmd.ExecuteNonQuery();
            // Runs that never produced a snapshot (failed to start) are noise.
            using var del = Cmd("DELETE FROM runs WHERE id=$id AND NOT EXISTS(SELECT 1 FROM snapshots WHERE run_id=$id)", ("$id", runId));
            del.ExecuteNonQuery();
        }
    }

    private const string RunSelect = """
        SELECT r.id, r.target_key, r.input, r.target_ip, r.mode, r.adapter_name, r.source_ip, r.public_ip, r.started_at, r.ended_at,
               (SELECT COUNT(*) FROM snapshots s WHERE s.run_id=r.id),
               s.loss, s.avg, s.best, s.jitter, s.hops, s.route_hash
        FROM runs r
        LEFT JOIN snapshots s ON s.run_id=r.id AND s.t=(SELECT MAX(t) FROM snapshots WHERE run_id=r.id)
        """;

    private static RunSummary ReadRun(SqliteDataReader rd)
    {
        double? D(int i) => rd.IsDBNull(i) ? null : rd.GetDouble(i);
        string? S(int i) => rd.IsDBNull(i) ? null : rd.GetString(i);
        var hopsJson = S(15);
        var hopCount = 0;
        if (hopsJson != null)
        {
            try { hopCount = JsonDocument.Parse(hopsJson).RootElement.GetArrayLength(); } catch { }
        }
        return new RunSummary(rd.GetInt64(0), rd.GetString(1), rd.GetString(2), S(3), rd.GetString(4), S(5), S(6), S(7), rd.GetInt64(8),
            rd.IsDBNull(9) ? null : rd.GetInt64(9), rd.GetInt32(10), D(11), D(12), D(13), D(14), hopCount, S(16));
    }

    public List<TargetSummary> Targets()
    {
        lock (_lock)
        {
            var runs = new List<RunSummary>();
            using (var cmd = Cmd(RunSelect + " ORDER BY r.started_at DESC LIMIT 5000"))
            using (var rd = cmd.ExecuteReader())
                while (rd.Read()) runs.Add(ReadRun(rd));
            return runs.GroupBy(r => r.TargetKey)
                .Select(g =>
                {
                    var ordered = g.OrderBy(r => r.StartedAt).ToList();
                    var last = ordered[^1];
                    return new TargetSummary(g.Key, ordered.Count, last.EndedAt ?? last.StartedAt, last.Avg, last.Loss,
                        ordered.TakeLast(24).Select(r => r.Avg).ToList());
                })
                .OrderByDescending(t => t.LastAt)
                .ToList();
        }
    }

    public List<RunSummary> Runs(string targetKey)
    {
        lock (_lock)
        {
            var list = new List<RunSummary>();
            using var cmd = Cmd(RunSelect + " WHERE r.target_key=$k ORDER BY r.started_at DESC", ("$k", targetKey));
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add(ReadRun(rd));
            return list;
        }
    }

    public RunSummary? LastRun(string targetKey, long? excludeId = null) =>
        Runs(targetKey).FirstOrDefault(r => r.Id != excludeId && r.Snapshots > 0);

    public RunDetail? Run(long id)
    {
        lock (_lock)
        {
            RunSummary? run = null;
            using (var cmd = Cmd(RunSelect + " WHERE r.id=$id", ("$id", id)))
            using (var rd = cmd.ExecuteReader())
                if (rd.Read()) run = ReadRun(rd);
            if (run == null) return null;
            var snaps = new List<SnapshotRow>();
            string? lastHops = null;
            using (var cmd = Cmd("SELECT t,loss,avg,best,worst,jitter,route_hash,rounds,hops FROM snapshots WHERE run_id=$id ORDER BY t", ("$id", id)))
            using (var rd = cmd.ExecuteReader())
            {
                while (rd.Read())
                {
                    double? D(int i) => rd.IsDBNull(i) ? null : rd.GetDouble(i);
                    snaps.Add(new SnapshotRow(rd.GetInt64(0), D(1), D(2), D(3), D(4), D(5), rd.IsDBNull(6) ? "" : rd.GetString(6), rd.IsDBNull(7) ? 0 : rd.GetInt32(7)));
                    lastHops = rd.IsDBNull(8) ? lastHops : rd.GetString(8);
                }
            }
            var hops = lastHops == null ? [] : JsonSerializer.Deserialize<List<HistoryHop>>(lastHops, Json.Options) ?? [];
            JsonElement? options = null;
            using (var cmd = Cmd("SELECT options FROM runs WHERE id=$id", ("$id", id)))
                if (cmd.ExecuteScalar() is string o) options = JsonDocument.Parse(o).RootElement.Clone();
            return new RunDetail(run, snaps, hops, options);
        }
    }

    public void DeleteRun(long id)
    {
        lock (_lock)
        {
            using var a = Cmd("DELETE FROM snapshots WHERE run_id=$id", ("$id", id));
            a.ExecuteNonQuery();
            using var b = Cmd("DELETE FROM runs WHERE id=$id", ("$id", id));
            b.ExecuteNonQuery();
        }
    }

    public void DeleteTarget(string key)
    {
        lock (_lock)
        {
            using var a = Cmd("DELETE FROM snapshots WHERE run_id IN (SELECT id FROM runs WHERE target_key=$k)", ("$k", key));
            a.ExecuteNonQuery();
            using var b = Cmd("DELETE FROM runs WHERE target_key=$k", ("$k", key));
            b.ExecuteNonQuery();
        }
    }

    public void Prune(TimeSpan keep)
    {
        var cutoff = DateTimeOffset.UtcNow.Add(-keep).ToUnixTimeMilliseconds();
        lock (_lock)
        {
            using var a = Cmd("DELETE FROM snapshots WHERE run_id IN (SELECT id FROM runs WHERE started_at < $c)", ("$c", cutoff));
            a.ExecuteNonQuery();
            using var b = Cmd("DELETE FROM runs WHERE started_at < $c", ("$c", cutoff));
            b.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_lock) _db.Dispose();
    }
}
