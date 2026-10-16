import { L, locale, plural } from '../i18n';
import { palette } from '../theme';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import uPlot from 'uplot';
import 'uplot/dist/uPlot.min.css';
import { on, request } from '../bridge/client';
import type { RunDetail, RunSummary, TargetSummary } from '../bridge/protocol';
import { useStore } from '../store';
import { duration, ms, pct } from '../lib/format';
import { useContextMenu } from './ContextMenu';
import { Play, Restart } from './Icons';
import './history.css';

function when(t: number): string {
  const d = new Date(t);
  const today = new Date();
  const y = new Date(today);
  y.setDate(today.getDate() - 1);
  const time = d.toLocaleTimeString(locale(), { hour: '2-digit', minute: '2-digit' });
  if (d.toDateString() === today.toDateString()) return `${L('сегодня', 'today')} ${time}`;
  if (d.toDateString() === y.toDateString()) return `${L('вчера', 'yesterday')} ${time}`;
  return `${d.toLocaleDateString(locale(), { day: 'numeric', month: 'short' })} ${time}`;
}

function Trend({ values }: { values: (number | null)[] }) {
  const nums = values.filter((v): v is number => v != null);
  if (nums.length < 2) return <svg width={64} height={18} />;
  const max = Math.max(...nums);
  const min = Math.min(...nums);
  const span = Math.max(1, max - min);
  const pts = values
    .map((v, i) => (v == null ? null : `${(i / (values.length - 1)) * 62 + 1},${17 - ((v - min) / span) * 15}`))
    .filter(Boolean)
    .join(' ');
  return (
    <svg width={64} height={18} aria-hidden>
      <polyline points={pts} fill="none" stroke="var(--accent)" strokeWidth={1.4} strokeLinejoin="round" />
    </svg>
  );
}

export function HistoryView() {
  const [targets, setTargets] = useState<TargetSummary[] | null>(null);
  const [target, setTarget] = useState<string | null>(null);
  const [runs, setRuns] = useState<RunSummary[]>([]);
  const [picked, setPicked] = useState<number[]>([]);
  const [filter, setFilter] = useState('');
  const start = useStore((s) => s.start);
  const notify = useStore((s) => s.notify);
  const openMenu = useContextMenu((s) => s.open);

  const loadTargets = useCallback(() => {
    void request('history.targets').then((t) => {
      setTargets(t);
      setTarget((cur) => cur ?? t[0]?.targetKey ?? null);
    });
  }, []);

  useEffect(() => {
    loadTargets();
    return on('history.changed', () => loadTargets());
  }, [loadTargets]);

  useEffect(() => {
    if (!target) return;
    setPicked([]);
    void request('history.runs', { target }).then(setRuns);
  }, [target, targets]);

  const list = (targets ?? []).filter((t) => !filter || t.targetKey.includes(filter.toLowerCase()));
  const current = targets?.find((t) => t.targetKey === target);

  const remove = async (args: { id?: number; target?: string }) => {
    await request('history.delete', args);
    loadTargets();
    if (args.target) setTarget(null);
  };

  if (targets && targets.length === 0)
    return (
      <div className="hist-empty">
        <h2>{L('История пока пустая', 'No history yet')}</h2>
        <p>
          {L('Каждая трассировка сохраняется сюда автоматически: раз в минуту и при остановке. Проверьте цель днём и вечером, и здесь можно будет сравнить задержки, потери и маршрут по хопам.', 'Every trace is saved here automatically: once a minute and when it stops. Check a target in the afternoon and in the evening, and compare latency, loss and the route hop by hop here.')}
        </p>
      </div>
    );

  return (
    <div className="hist">
      <aside className="hist-targets">
        <input className="field sm" placeholder={L('Поиск цели', 'Find a target')} value={filter} onChange={(e) => setFilter(e.target.value)} />
        <div className="hist-list">
          {list.map((t) => (
            <button
              key={t.targetKey}
              className={`ht-row ${t.targetKey === target ? 'on' : ''}`}
              onClick={() => setTarget(t.targetKey)}
              onContextMenu={(e) =>
                openMenu(e, [
                  { title: t.targetKey },
                  { label: L('Проверить сейчас', 'Check now'), icon: <Play size={11} />, onSelect: () => start(t.targetKey) },
                  { separator: true },
                  { label: L('Удалить всю историю цели', 'Delete all history of this target'), onSelect: () => void remove({ target: t.targetKey }) },
                ])
              }
            >
              <span className="ht-name mono">{t.targetKey}</span>
              <span className="ht-meta">
                {when(t.lastAt)} · {t.runs} {plural(t.runs, ['запуск', 'запуска', 'запусков'], ['run', 'runs'])}
              </span>
              <span className="ht-vals num">
                {ms(t.lastAvg)} {L('мс', 'ms')} · {pct(t.lastLoss)}%
              </span>
              <Trend values={t.avgTrend} />
            </button>
          ))}
        </div>
      </aside>
      <section className="hist-main">
        {current && (
          <>
            <header className="hist-head">
              <div>
                <h2 className="mono">{current.targetKey}</h2>
                <span className="muted">
                  {current.runs} {plural(current.runs, ['запуск', 'запуска', 'запусков'], ['run', 'runs'])}, {L('последний', 'last')} {when(current.lastAt)}
                </span>
              </div>
              <button className="btn primary" onClick={() => start(current.targetKey)}>
                <Play size={12} /> {L('Проверить сейчас', 'Check now')}
              </button>
            </header>
            <RunsChart runs={runs} />
            <div className="hist-hint">{L('Отметьте два запуска, чтобы сравнить их по хопам. ПКМ по строке: удалить запуск.', 'Tick two runs to compare them hop by hop. Right-click a row to delete a run.')}</div>
            <div className="runs-scroll">
              <table className="runs">
                <thead>
                  <tr>
                    <th />
                    <th>{L('Когда', 'When')}</th>
                    <th>{L('Длилось', 'Duration')}</th>
                    <th>{L('Режим', 'Mode')}</th>
                    <th>{L('Адаптер · источник', 'Adapter · source')}</th>
                    <th className="r">Avg</th>
                    <th className="r">Best</th>
                    <th className="r">Loss%</th>
                    <th className="r">Jitter</th>
                    <th className="r">{L('Хопов', 'Hops')}</th>
                    <th>{L('Маршрут', 'Route')}</th>
                  </tr>
                </thead>
                <tbody>
                  {runs.map((r, i) => {
                    const prev = runs[i + 1];
                    const changed = prev && prev.routeHash && r.routeHash && prev.routeHash !== r.routeHash;
                    const on = picked.includes(r.id);
                    return (
                      <tr
                        key={r.id}
                        className={on ? 'on' : ''}
                        onClick={() => setPicked((p) => (on ? p.filter((x) => x !== r.id) : [...p.slice(-1), r.id]))}
                        onContextMenu={(e) =>
                          openMenu(e, [
                            { title: when(r.startedAt) },
                            {
                              label: L('Удалить запуск', 'Delete run'),
                              onSelect: () =>
                                void remove({ id: r.id })
                                  .then(() => request('history.runs', { target: target! }).then(setRuns))
                                  .catch((err: Error) => notify(err.message, 'error')),
                            },
                          ])
                        }
                      >
                        <td>
                          <span className={`check ${on ? 'on' : ''}`} />
                        </td>
                        <td>{when(r.startedAt)}</td>
                        <td className="num muted">{r.endedAt ? duration(r.endedAt - r.startedAt) : L('идёт', 'running')}</td>
                        <td>{r.mode === 'vpn' ? <span className="badge accent">VPN</span> : <span className="muted">{L('обычный', 'normal')}</span>}</td>
                        <td className="muted">
                          {r.adapterName ?? L('Авто', 'Auto')} <span className="mono">{r.sourceIp ?? ''}</span>
                        </td>
                        <td className="r num">{ms(r.avg)}</td>
                        <td className="r num">{ms(r.best)}</td>
                        <td className={`r num ${r.loss && r.loss >= 5 ? 'bad' : r.loss ? 'warn' : ''}`}>{pct(r.loss)}</td>
                        <td className="r num">{ms(r.jitter)}</td>
                        <td className="r num">{r.hops}</td>
                        <td>{changed ? <span className="badge warn">{L('изменился', 'changed')}</span> : <span className="mono muted">{r.routeHash?.slice(0, 6)}</span>}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
            {picked.length === 2 && <Compare a={picked[0]} b={picked[1]} />}
          </>
        )}
      </section>
    </div>
  );
}

function RunsChart({ runs }: { runs: RunSummary[] }) {
  const box = useRef<HTMLDivElement>(null);
  const data = useMemo(() => {
    const sorted = [...runs].filter((r) => r.avg != null).sort((a, b) => a.startedAt - b.startedAt);
    return [sorted.map((r) => r.startedAt / 1000), sorted.map((r) => r.avg), sorted.map((r) => r.loss)] as uPlot.AlignedData;
  }, [runs]);
  useEffect(() => {
    const el = box.current!;
    const plot = new uPlot(
      {
        width: el.clientWidth,
        height: 170,
        legend: { show: true },
        scales: { x: { time: true }, ms: { range: (_u, _a, max) => [0, Math.max(5, max * 1.15)] }, pct: { range: [0, 100] } },
        axes: [
          { stroke: palette.axis, grid: { stroke: palette.grid }, ticks: { stroke: palette.grid }, font: '11px Geist Variable, Segoe UI' },
          { scale: 'ms', stroke: palette.axis, grid: { stroke: palette.grid }, ticks: { stroke: palette.grid }, font: '11px Geist Variable, Segoe UI', values: (_u, v) => v.map((x) => `${x} ${L('мс', 'ms')}`), size: 50 },
          { scale: 'pct', side: 1, stroke: palette.axis, grid: { show: false }, font: '11px Geist Variable, Segoe UI', values: (_u, v) => v.map((x) => `${x}%`), size: 40 },
        ],
        series: [
          { value: (_u, v) => (v == null ? '—' : new Date(v * 1000).toLocaleString(locale())) },
          { label: L('Средняя задержка', 'Average latency'), scale: 'ms', stroke: palette.accent, width: 1.6, points: { show: true, size: 6, fill: palette.accent }, value: (_u, v) => (v == null ? '—' : `${v.toFixed(1)} ${L('мс', 'ms')}`) },
          { label: L('Потери', 'Loss'), scale: 'pct', stroke: palette.lost, width: 1, points: { show: true, size: 4 }, value: (_u, v) => (v == null ? '—' : `${v}%`) },
        ],
      },
      data,
      el,
    );
    const ro = new ResizeObserver(() => plot.setSize({ width: el.clientWidth, height: 170 }));
    ro.observe(el);
    return () => {
      ro.disconnect();
      plot.destroy();
    };
  }, [data]);
  return <div className="hist-chart" ref={box} />;
}

function Compare({ a, b }: { a: number; b: number }) {
  const [ra, setA] = useState<RunDetail | null>(null);
  const [rb, setB] = useState<RunDetail | null>(null);
  useEffect(() => {
    void Promise.all([request('history.run', { id: a }), request('history.run', { id: b })]).then(([x, y]) => {
      // Older run on the left.
      if (x && y && x.run.startedAt > y.run.startedAt) {
        setA(y);
        setB(x);
      } else {
        setA(x);
        setB(y);
      }
    });
  }, [a, b]);
  if (!ra || !rb) return <div className="empty">{L('Загрузка сравнения…', 'Loading comparison…')}</div>;
  const max = Math.max(ra.hops.length, rb.hops.length);
  const rows = Array.from({ length: max }, (_, i) => i + 1).map((ttl) => ({ ttl, x: ra.hops.find((h) => h.ttl === ttl), y: rb.hops.find((h) => h.ttl === ttl) }));
  const delta = (p: number | null | undefined, q: number | null | undefined) => (p == null || q == null ? null : q - p);
  const fmtDelta = (d: number | null, unit: string) => (d == null ? '' : `${d > 0 ? '+' : ''}${Math.abs(d) < 10 ? d.toFixed(1) : Math.round(d)}${unit}`);
  return (
    <div className="compare">
      <div className="cmp-head">
        <span>
          <b>A</b> {when(ra.run.startedAt)} · {ra.run.adapterName ?? L('Авто', 'Auto')}
        </span>
        <Restart size={13} />
        <span>
          <b>{L('Б', 'B')}</b> {when(rb.run.startedAt)} · {rb.run.adapterName ?? L('Авто', 'Auto')}
        </span>
        <span className="cmp-sum num">
          {L('цель', 'target')}: {ms(ra.run.avg)} → {ms(rb.run.avg)} {L('мс', 'ms')} ({fmtDelta(delta(ra.run.avg, rb.run.avg), ` ${L('мс', 'ms')}`)}), {L('потери', 'loss')} {pct(ra.run.loss)}% → {pct(rb.run.loss)}%
        </span>
      </div>
      <table className="cmp">
        <thead>
          <tr>
            <th className="r">#</th>
            <th>{L('А: узел', 'A: host')}</th>
            <th className="r">Avg</th>
            <th className="r">Loss</th>
            <th>{L('Б: узел', 'B: host')}</th>
            <th className="r">Avg</th>
            <th className="r">Loss</th>
            <th className="r">Δ Avg</th>
            <th className="r">Δ Loss</th>
          </tr>
        </thead>
        <tbody>
          {rows.map(({ ttl, x, y }) => {
            const diffNode = (x?.ip ?? null) !== (y?.ip ?? null);
            const dAvg = delta(x?.avg, y?.avg);
            const dLoss = delta(x?.loss, y?.loss);
            return (
              <tr key={ttl} className={diffNode ? 'diff' : ''}>
                <td className="r num muted">{ttl}</td>
                <td className="mono" title={x?.host ?? undefined}>
                  {x?.ip ?? '*'}
                </td>
                <td className="r num">{ms(x?.avg)}</td>
                <td className="r num">{x ? pct(x.loss) : ''}</td>
                <td className="mono" title={y?.host ?? undefined}>
                  {y?.ip ?? '*'}
                  {diffNode && <span className="badge warn">{L('другой узел', 'different host')}</span>}
                </td>
                <td className="r num">{ms(y?.avg)}</td>
                <td className="r num">{y ? pct(y.loss) : ''}</td>
                <td className={`r num ${dAvg != null && dAvg > 10 ? 'bad' : dAvg != null && dAvg < -10 ? 'good' : ''}`}>{fmtDelta(dAvg, '')}</td>
                <td className={`r num ${dLoss != null && dLoss > 2 ? 'bad' : dLoss != null && dLoss < -2 ? 'good' : ''}`}>{fmtDelta(dLoss, '%')}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
