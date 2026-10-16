using MTRTracer.Core.Protocol;

namespace MTRTracer.Core.Mtr;

/// <summary>
/// Turns raw hop statistics into plain-language hints, following the usual rules for reading MTR output:
/// loss that does not persist to the destination is ICMP rate limiting, not a fault.
/// </summary>
public sealed class Analyzer
{
    public const int MinSamples = 10;

    public List<NoteDto> Analyze(IReadOnlyList<HopDto> hops, int? destTtl, TcpStatsDto? tcp)
    {
        var notes = new List<NoteDto>();
        if (hops.Count == 0) return notes;
        var last = hops[^1];
        var dest = destTtl != null ? hops.FirstOrDefault(h => h.Ttl == destTtl) : null;
        var end = dest ?? hops.LastOrDefault(h => h.Recv > 0);
        if (end == null || end.Sent < 3)
        {
            if (hops[0].Sent >= 5 && hops.All(h => h.Recv == 0))
                notes.Add(new("error", "no-replies", Loc.S("Ни один узел не ответил. Проверьте подключение выбранного адаптера, файрвол или VPN.", "No router answered. Check the selected adapter's connection, the firewall or the VPN."), null));
            return notes;
        }

        // Loss that originates at a hop and carries through to the end of the path.
        HopDto? lossOrigin = null;
        if (end.Sent >= MinSamples && end.Loss >= 3)
        {
            foreach (var h in hops)
            {
                if (h.Sent < MinSamples || h.Recv == 0 || h.Loss < 3) continue;
                var later = hops.Where(x => x.Ttl > h.Ttl && x.Recv > 0 && x.Sent >= MinSamples).ToList();
                if (later.All(x => x.Loss >= h.Loss * 0.6))
                {
                    lossOrigin = h;
                    break;
                }
            }
            notes.Add(lossOrigin != null
                ? new("error", "loss-origin", Loc.S($"Потери {end.Loss:0.#}% до цели начинаются на хопе {lossOrigin.Ttl} ({lossOrigin.Primary}) и сохраняются дальше. Вероятное место проблемы.", $"The {end.Loss:0.#}% loss to the target starts at hop {lossOrigin.Ttl} ({lossOrigin.Primary}) and persists further on. Likely place of the problem."), lossOrigin.Ttl)
                : new("error", "dest-loss", Loc.S($"Потери до цели {end.Loss:0.#}%.", $"Loss to the target: {end.Loss:0.#}%."), end.Ttl));
        }

        // Loss on intermediate hops that does not propagate: control-plane rate limiting.
        foreach (var h in hops)
        {
            if (h == end || h.Sent < MinSamples || h.Recv == 0 || h.Loss < 5) continue;
            if (lossOrigin != null && h.Ttl >= lossOrigin.Ttl) continue;
            var after = hops.Where(x => x.Ttl > h.Ttl && x.Recv > 0).ToList();
            if (after.Count > 0 && after.Min(x => x.Loss) < h.Loss / 2)
                notes.Add(new("info", "rate-limit", Loc.S($"Хоп {h.Ttl}: {h.Loss:0.#}% потерь, но дальше пакеты проходят. Роутер ограничивает ответы ICMP, на трафик это не влияет.", $"Hop {h.Ttl}: {h.Loss:0.#}% loss, but packets get through further on. The router rate-limits ICMP replies; traffic is not affected."), h.Ttl));
        }

        // Silent routers: never answer but the path continues.
        var silent = hops.Where(h => h.Sent >= 5 && h.Recv == 0 && hops.Any(x => x.Ttl > h.Ttl && x.Recv > 0)).Select(h => h.Ttl).ToList();
        if (silent.Count > 0)
            notes.Add(new("info", "silent-hop", Loc.S($"Хоп {string.Join(", ", silent)} не отвечает на ICMP, но пропускает трафик дальше. Это нормально.", $"Hop {string.Join(", ", silent)} does not answer ICMP but forwards traffic. This is normal."), silent[0]));

        // Latency step that persists to the end.
        HopDto? prev = null;
        foreach (var h in hops.Where(x => x.Avg != null && x.Recv >= 3))
        {
            if (prev != null && h.Avg - prev.Avg >= 25)
            {
                var later = hops.Where(x => x.Ttl > h.Ttl && x.Avg != null).ToList();
                if (later.All(x => x.Avg >= h.Avg - 10))
                    notes.Add(new("warn", "latency-jump", Loc.S($"Задержка растёт на +{h.Avg - prev.Avg:0} мс между хопами {prev.Ttl} и {h.Ttl}. Часто это длинный магистральный канал или перегрузка.", $"Latency grows by +{h.Avg - prev.Avg:0} ms between hops {prev.Ttl} and {h.Ttl}. Often a long backbone link or congestion."), h.Ttl));
            }
            prev = h;
        }

        if (end.Recv >= MinSamples && end.JAvg is > 10)
            notes.Add(new("warn", "jitter", Loc.S($"Высокий джиттер до цели: в среднем {end.JAvg:0.0} мс, максимум {end.JMax:0} мс. Возможны лаги в играх и звонках.", $"High jitter to the target: {end.JAvg:0.0} ms on average, {end.JMax:0} ms max. Expect lag in games and calls."), end.Ttl));

        if (dest == null && tcp is { Recv: > 0 })
            notes.Add(new("info", "icmp-filtered", Loc.S($"Цель не отвечает на ICMP, но TCP-порт {tcp.Port} отвечает за {tcp.Avg:0.0} мс. Узел доступен, ICMP фильтруется.", $"The target does not answer ICMP, but TCP port {tcp.Port} answers in {tcp.Avg:0.0} ms. The host is reachable; ICMP is filtered."), null));
        else if (dest == null && last.Recv == 0 && last.Sent >= 5)
            notes.Add(new("warn", "dest-silent", Loc.S("Цель пока не ответила. Узел может блокировать ICMP или быть дальше лимита хопов.", "The target has not answered yet. It may block ICMP or be beyond the hop limit."), null));

        if (dest != null && dest.Sent >= MinSamples && dest.Loss < 1 && notes.All(n => n.Level != "error" && n.Code != "jitter"))
            notes.Insert(0, new("ok", "healthy", Loc.S($"Маршрут стабилен: {dest.Loss:0.#}% потерь, средняя задержка {dest.Avg:0.0} мс.", $"Route is stable: {dest.Loss:0.#}% loss, {dest.Avg:0.0} ms average latency."), dest.Ttl));

        return notes;
    }
}
