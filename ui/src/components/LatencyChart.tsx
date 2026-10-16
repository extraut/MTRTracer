import { useEffect, useRef, useState } from 'react';
import uPlot from 'uplot';
import 'uplot/dist/uPlot.min.css';
import { series, useStore } from '../store';

const windows = [
  [60, '1 мин'],
  [300, '5 мин'],
  [900, '15 мин'],
  [0, 'Всё'],
] as const;

const palette = ['#9b9cf0', '#57d29a', '#ecbf4f', '#ee8c4b', '#6fc3f0', '#e58fd8', '#b4d860'];

/**
 * RTT over time. Probes are bucketed by the probe interval so every hop shares one time axis;
 * gaps are lost packets. Destination is always drawn, plus the selected or hovered hop and any pinned ones.
 */
export function LatencyChart() {
  const box = useRef<HTMLDivElement>(null);
  const plot = useRef<uPlot | null>(null);
  const version = useStore((s) => s.probeVersion);
  const destTtl = useStore((s) => s.destTtl);
  const hopsCount = useStore((s) => s.hops.length);
  const selected = useStore((s) => s.selectedTtl);
  const interval = useStore((s) => s.options.intervalMs);
  const [win, setWin] = useState<number>(300);
  const [all, setAll] = useState(false);
  const lastDraw = useRef(0);

  const lastTtl = destTtl ?? hopsCount;
  const ttls = all ? Array.from({ length: hopsCount }, (_, i) => i + 1) : [...new Set([lastTtl, selected].filter((x): x is number => !!x))];
  const key = `${ttls.join(',')}|${all}`;

  // (Re)create the plot when the set of series changes.
  useEffect(() => {
    const el = box.current!;
    const opts: uPlot.Options = {
      width: el.clientWidth,
      height: Math.max(80, el.clientHeight),
      pxAlign: 0,
      legend: { show: true, live: true },
      cursor: { drag: { x: true, y: false }, points: { size: 6 } },
      scales: { x: { time: true }, y: { range: (_u, _min, max) => [0, Math.max(5, max * 1.12)] } },
      axes: [
        { stroke: '#7d8090', grid: { stroke: 'rgba(120,124,140,0.12)', width: 1 }, ticks: { stroke: 'rgba(120,124,140,0.2)' }, font: '11px Geist Variable, Segoe UI', size: 28 },
        {
          stroke: '#7d8090',
          grid: { stroke: 'rgba(120,124,140,0.12)', width: 1 },
          ticks: { show: false },
          font: '11px Geist Variable, Segoe UI',
          size: 46,
          values: (_u, vals) => vals.map((v) => `${v} мс`),
        },
      ],
      series: [
        { value: (_u, v) => (v == null ? '—' : new Date(v * 1000).toLocaleTimeString('ru-RU', { hour12: false })) },
        ...ttls.map((t, i) => ({
          label: t === lastTtl ? `Цель (хоп ${t})` : `Хоп ${t}`,
          stroke: t === lastTtl ? palette[0] : palette[(i % (palette.length - 1)) + 1],
          width: t === lastTtl ? 1.75 : 1.25,
          spanGaps: false,
          points: { show: false },
          value: (_u: uPlot, v: number | null, _s: number, idx: number | null) => (idx == null ? '—' : v == null ? 'нет ответа' : `${v.toFixed(2)} мс`),
        })),
      ],
    };
    plot.current?.destroy();
    plot.current = new uPlot(opts, [[], ...ttls.map(() => [])] as uPlot.AlignedData, el);
    const ro = new ResizeObserver(() => plot.current?.setSize({ width: el.clientWidth, height: Math.max(80, el.clientHeight - 30) }));
    ro.observe(el);
    lastDraw.current = 0;
    return () => {
      ro.disconnect();
      plot.current?.destroy();
      plot.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  // Feed data (throttled to 2 Hz).
  useEffect(() => {
    const now = performance.now();
    if (now - lastDraw.current < 500 && lastDraw.current !== 0) return;
    lastDraw.current = now;
    const p = plot.current;
    if (!p) return;
    const step = Math.max(0.1, interval / 1000);
    let tMax = 0;
    let tMin = Infinity;
    for (const t of ttls) {
      const s = series.get(t);
      if (!s || !s.t.length) continue;
      tMax = Math.max(tMax, s.t[s.t.length - 1]);
      tMin = Math.min(tMin, s.t[0]);
    }
    if (!tMax) {
      p.setData([[], ...ttls.map(() => [])] as uPlot.AlignedData);
      return;
    }
    const from = win ? Math.max(tMin, tMax - win) : tMin;
    const bins = Math.max(1, Math.min(6000, Math.floor((tMax - from) / step) + 1));
    const xs = new Array<number>(bins);
    for (let i = 0; i < bins; i++) xs[i] = from + i * step;
    const ys = ttls.map((t) => {
      const out = new Array<number | null>(bins).fill(null);
      const cnt = new Array<number>(bins).fill(0);
      const s = series.get(t);
      if (!s) return out;
      for (let i = s.t.length - 1; i >= 0 && s.t[i] >= from; i--) {
        const b = Math.min(bins - 1, Math.floor((s.t[i] - from) / step));
        const v = s.v[i];
        if (Number.isNaN(v)) continue;
        out[b] = ((out[b] ?? 0) * cnt[b] + v) / (cnt[b] + 1);
        cnt[b]++;
      }
      return out;
    });
    p.setData([xs, ...ys] as uPlot.AlignedData);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [version, key, win, interval]);

  return (
    <div className="panel chart">
      <div className="panel-tools">
        <div className="seg">
          {windows.map(([w, l]) => (
            <button key={w} className={win === w ? 'on' : ''} onClick={() => setWin(w)}>
              {l}
            </button>
          ))}
        </div>
        <button className={`btn sm ${all ? 'active' : ''}`} onClick={() => setAll(!all)}>
          Все хопы
        </button>
        <span className="panel-count">Выделите хоп в таблице, чтобы сравнить его с целью. Протяните мышью по графику для приближения.</span>
      </div>
      <div className="chart-box" ref={box} />
    </div>
  );
}
