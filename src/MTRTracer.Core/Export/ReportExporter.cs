using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MTRTracer.Core.Geo;
using MTRTracer.Core.Logging;
using MTRTracer.Core.Protocol;

namespace MTRTracer.Core.Export;

public sealed record ExportContext(
    TraceStateDto State,
    HopsSnapshotDto Snapshot,
    TraceOptions Options,
    IReadOnlyList<ProbeRowDto> Probes,
    IReadOnlyList<TraceEventDto> Events,
    Func<string, HostInfoDto?> Host,
    string? AdapterName,
    RouteGeoDto? Geo = null);

/// <summary>Session exports: WinMTR-like text, CSV, full JSON (with every probe) and a standalone HTML report.</summary>
public static class ReportExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string F(double? v) => v == null ? "—" : v.Value.ToString("0.0", Inv);

    private static string HostLabel(ExportContext c, HopDto h)
    {
        if (h.Primary == null) return "Нет ответа";
        var name = c.Host(h.Primary)?.Hostname;
        return name != null ? $"{name} [{h.Primary}]" : h.Primary;
    }

    private static string Header(ExportContext c)
    {
        var s = c.State;
        var started = DateTimeOffset.FromUnixTimeMilliseconds(s.StartedAt).ToLocalTime();
        return $"MTRTracer: {s.Host} [{s.TargetIp}]  источник {s.SourceIp ?? "системный"}{(c.AdapterName != null ? $" ({c.AdapterName})" : "")}\n" +
               $"Начало: {started:yyyy-MM-dd HH:mm:ss}, длительность {c.Snapshot.ElapsedMs / 1000.0:0} с, раундов {c.Snapshot.Round}, " +
               $"интервал {c.Options.IntervalMs} мс, размер {c.Options.PacketSize} Б, таймаут {c.Options.TimeoutMs} мс";
    }

    public static string ToText(ExportContext c)
    {
        var rows = c.Snapshot.Hops;
        var hostWidth = Math.Clamp(rows.Select(h => HostLabel(c, h).Length).DefaultIfEmpty(20).Max(), 20, 70);
        var sb = new StringBuilder();
        sb.AppendLine(Header(c));
        sb.AppendLine();
        var head = $"{"#",3}  {"Узел".PadRight(hostWidth)}  {"Loss%",6} {"Sent",5} {"Recv",5} {"Best",7} {"Avg",7} {"Worst",7} {"Last",7} {"StDev",7} {"Jttr",6}";
        sb.AppendLine(head);
        sb.AppendLine(new string('-', head.Length));
        foreach (var h in rows)
        {
            sb.AppendLine($"{h.Ttl,3}  {Trunc(HostLabel(c, h), hostWidth).PadRight(hostWidth)}  {h.Loss.ToString("0.0", Inv),6} {h.Sent,5} {h.Recv,5} {F(h.Best),7} {F(h.Avg),7} {F(h.Worst),7} {F(h.Last),7} {F(h.StDev),7} {F(h.JAvg),6}");
            foreach (var r in h.Responders.Skip(1))
                sb.AppendLine($"{"",3}  {Trunc("  ↳ " + (c.Host(r.Ip)?.Hostname is { } n ? $"{n} [{r.Ip}]" : r.Ip) + $" ({r.Share:0}%)", hostWidth).PadRight(hostWidth)}");
        }
        if (c.Snapshot.Tcp is { } t)
            sb.AppendLine($"\nTCP :{t.Port}  loss {t.Loss:0.0}%  sent {t.Sent}  recv {t.Recv}  best {F(t.Best)}  avg {F(t.Avg)}  worst {F(t.Worst)}");
        if (c.Snapshot.Notes.Count > 0)
        {
            sb.AppendLine("\nВыводы:");
            foreach (var n in c.Snapshot.Notes) sb.AppendLine($"  • {n.Text}");
        }
        return sb.ToString();
    }

    private static string Trunc(string s, int w) => s.Length <= w ? s : s[..(w - 1)] + "…";

    public static string ToCsv(ExportContext c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("hop,ip,hostname,asn,as_name,country,city,lat,lon,loss_pct,sent,recv,best_ms,avg_ms,worst_ms,last_ms,stdev_ms,gmean_ms,jitter_ms,javg_ms,jmax_ms,jint_ms,status,alt_responders");
        foreach (var h in c.Snapshot.Hops)
        {
            var info = h.Primary == null ? null : c.Host(h.Primary);
            var geo = c.Geo?.Hops.FirstOrDefault(x => x.Ttl == h.Ttl && x.Lat != null);
            string D(double? v) => v?.ToString("0.###", Inv) ?? "";
            var fields = new[]
            {
                h.Ttl.ToString(Inv), h.Primary ?? "", info?.Hostname ?? "", info?.Asn?.ToString(Inv) ?? "", info?.AsName ?? "",
                geo?.CountryCode ?? "", geo?.City ?? "", D(geo?.Lat), D(geo?.Lon),
                h.Loss.ToString("0.0", Inv), h.Sent.ToString(Inv), h.Recv.ToString(Inv),
                D(h.Best), D(h.Avg), D(h.Worst), D(h.Last), D(h.StDev), D(h.GMean), D(h.Jitter), D(h.JAvg), D(h.JMax), D(h.JInt),
                h.Status, string.Join(" ", h.Responders.Skip(1).Select(r => r.Ip)),
            };
            sb.AppendLine(string.Join(",", fields.Select(Csv)));
        }
        return sb.ToString();
    }

    private static string Csv(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string ToJson(ExportContext c)
    {
        var hosts = c.Snapshot.Hops.SelectMany(h => h.Responders.Select(r => r.Ip)).Distinct()
            .Select(ip => c.Host(ip)).Where(i => i != null).ToList();
        var doc = new
        {
            generator = "MTRTracer",
            exportedAt = DateTimeOffset.Now,
            session = c.State,
            options = c.Options,
            summary = c.Snapshot,
            hosts,
            events = c.Events,
            probes = c.Probes,
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions(Json.Options) { WriteIndented = true });
    }

    public static string ToHtml(ExportContext c)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>MTRTracer: ").Append(E(c.State.Host)).Append("</title><style>")
            .Append("body{font:14px/1.45 'Segoe UI',system-ui,sans-serif;background:#16171b;color:#e6e6ea;margin:32px}")
            .Append("h1{font-size:20px;margin:0 0 4px}p.m{color:#9a9ca6;margin:0 0 24px;white-space:pre-line}")
            .Append("table{border-collapse:collapse;font-variant-numeric:tabular-nums;width:100%}")
            .Append("th,td{padding:6px 10px;text-align:right;border-bottom:1px solid #2a2c33}th{color:#9a9ca6;font-weight:500;font-size:12px}")
            .Append("td.l,th.l{text-align:left}.ip{font-family:Consolas,monospace;color:#9a9ca6;font-size:12px}")
            .Append(".bad{color:#ff7b72}.warn{color:#e3b341}.ok{color:#56d364}ul{padding-left:18px}")
            .Append("</style></head><body>");
        sb.Append("<h1>").Append(E(c.State.Host)).Append(" <span class=\"ip\">").Append(E(c.State.TargetIp)).Append("</span></h1>");
        sb.Append("<p class=\"m\">").Append(E(Header(c))).Append("</p>");
        sb.Append("<table><thead><tr><th>#</th><th class=\"l\">Узел</th><th class=\"l\">AS / место</th><th>Loss%</th><th>Sent</th><th>Recv</th><th>Best</th><th>Avg</th><th>Worst</th><th>Last</th><th>StDev</th><th>Jitter</th></tr></thead><tbody>");
        foreach (var h in c.Snapshot.Hops)
        {
            var info = h.Primary == null ? null : c.Host(h.Primary);
            var geo = c.Geo?.Hops.FirstOrDefault(x => x.Ttl == h.Ttl && x.Lat != null);
            var lossCls = h.Loss >= 5 ? "bad" : h.Loss > 0 ? "warn" : "";
            sb.Append("<tr><td>").Append(h.Ttl).Append("</td><td class=\"l\">")
                .Append(h.Primary == null ? "<span class=\"ip\">нет ответа</span>" : $"{E(info?.Hostname ?? h.Primary)}<br><span class=\"ip\">{E(h.Primary)}</span>")
                .Append("</td><td class=\"l\">").Append(E(info?.Asn != null ? $"AS{info.Asn} {info.AsName}" : ""))
                .Append("<br><span class=\"ip\">").Append(E(string.Join(", ", new[] { geo?.City, geo?.CountryCode }.Where(x => !string.IsNullOrEmpty(x))))).Append("</span></td>")
                .Append($"<td class=\"{lossCls}\">{h.Loss.ToString("0.0", Inv)}</td><td>{h.Sent}</td><td>{h.Recv}</td>")
                .Append($"<td>{F(h.Best)}</td><td>{F(h.Avg)}</td><td>{F(h.Worst)}</td><td>{F(h.Last)}</td><td>{F(h.StDev)}</td><td>{F(h.JAvg)}</td></tr>");
        }
        sb.Append("</tbody></table>");
        if (c.Snapshot.Notes.Count > 0)
        {
            sb.Append("<h2 style=\"font-size:15px;margin-top:28px\">Выводы</h2><ul>");
            foreach (var n in c.Snapshot.Notes)
                sb.Append("<li class=\"").Append(n.Level switch { "error" => "bad", "warn" => "warn", "ok" => "ok", _ => "" }).Append("\">").Append(E(n.Text)).Append("</li>");
            sb.Append("</ul>");
        }
        if (c.Events.Count > 0)
        {
            sb.Append("<h2 style=\"font-size:15px;margin-top:28px\">События</h2><table><tbody>");
            foreach (var e in c.Events.TakeLast(200))
                sb.Append("<tr><td class=\"l ip\">").Append(DateTimeOffset.FromUnixTimeMilliseconds(e.T).ToLocalTime().ToString("HH:mm:ss")).Append("</td><td class=\"l\">").Append(E(e.Message)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("<p class=\"m\" style=\"margin-top:28px\">IP Geolocation by DB-IP (db-ip.com), ip-api.com. Отчёт создан MTRTracer.</p></body></html>");
        return sb.ToString();
    }
}
