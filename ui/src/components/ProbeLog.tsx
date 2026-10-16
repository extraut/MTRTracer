import { useEffect, useMemo, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { probeBuffer, useStore } from '../store';
import { clock, statusText } from '../lib/format';
import { Search } from './Icons';

type Filter = 'all' | 'replies' | 'lost' | 'errors';

function useThrottled<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  const last = useRef(0);
  useEffect(() => {
    const now = performance.now();
    const wait = Math.max(0, ms - (now - last.current));
    const t = window.setTimeout(() => {
      last.current = performance.now();
      setV(value);
    }, wait);
    return () => window.clearTimeout(t);
  }, [value, ms]);
  return v;
}

const isError = (s: string) => !['ok', 'ttl-expired', 'timeout', 'tcp-open', 'tcp-refused'].includes(s);

export function ProbeLog() {
  const version = useThrottled(useStore((s) => s.probeVersion), 200);
  const autoScroll = useStore((s) => s.ui.logAutoScroll);
  const setUi = useStore((s) => s.setUi);
  const hops = useStore((s) => s.hops.length);
  const [ttl, setTtl] = useState(0);
  const [filter, setFilter] = useState<Filter>('all');
  const [query, setQuery] = useState('');
  const scroller = useRef<HTMLDivElement>(null);

  const rows = useMemo(() => {
    const q = query.trim();
    if (!ttl && filter === 'all' && !q) return probeBuffer.slice();
    return probeBuffer.filter(
      (r) =>
        (!ttl || r.ttl === ttl) &&
        (filter === 'all' || (filter === 'replies' ? r.from != null : filter === 'lost' ? r.from == null : isError(r.status))) &&
        (!q || (r.from ?? '').includes(q) || r.status.includes(q) || String(r.seq) === q),
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [version, ttl, filter, query]);

  const virt = useVirtualizer({ count: rows.length, getScrollElement: () => scroller.current, estimateSize: () => 24, overscan: 20 });

  useEffect(() => {
    if (autoScroll && rows.length) virt.scrollToIndex(rows.length - 1, { align: 'end' });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rows.length, autoScroll]);

  return (
    <div className="panel log">
      <div className="panel-tools">
        <select className="field sm" value={ttl} onChange={(e) => setTtl(Number(e.target.value))} aria-label="Хоп">
          <option value={0}>Все хопы</option>
          {Array.from({ length: hops }, (_, i) => (
            <option key={i + 1} value={i + 1}>
              Хоп {i + 1}
            </option>
          ))}
        </select>
        <div className="seg">
          {(
            [
              ['all', 'Все'],
              ['replies', 'Ответы'],
              ['lost', 'Потери'],
              ['errors', 'Ошибки ICMP'],
            ] as const
          ).map(([k, l]) => (
            <button key={k} className={filter === k ? 'on' : ''} onClick={() => setFilter(k)}>
              {l}
            </button>
          ))}
        </div>
        <label className="search">
          <Search size={13} />
          <input className="field sm mono" placeholder="IP, статус или seq" value={query} onChange={(e) => setQuery(e.target.value)} />
        </label>
        <span className="panel-count num">{rows.length.toLocaleString('ru-RU')} пакетов</span>
        <button className={`btn sm ${autoScroll ? 'active' : ''}`} onClick={() => setUi({ logAutoScroll: !autoScroll })} title="Прокручивать к новым пакетам">
          Следить
        </button>
      </div>
      <div className="log-head">
        <span>Время</span>
        <span className="r">Раунд</span>
        <span className="r">Seq</span>
        <span className="r">TTL</span>
        <span>Ответил</span>
        <span>Статус ICMP</span>
        <span className="r">RTT, мс</span>
        <span className="r">Ядро</span>
        <span className="r">Байт</span>
        <span>Источник</span>
        <span>Proto</span>
      </div>
      <div className="log-body" ref={scroller} onWheel={() => autoScroll && setUi({ logAutoScroll: false })}>
        {rows.length === 0 ? (
          <div className="empty">Здесь будет каждый отправленный пакет: время с миллисекундами, TTL, кто ответил, код ICMP и RTT с точностью до микросекунды.</div>
        ) : (
          <div style={{ height: virt.getTotalSize(), position: 'relative' }}>
            {virt.getVirtualItems().map((vi) => {
              const r = rows[vi.index];
              const lost = r.from == null;
              const err = isError(r.status);
              return (
                <div key={vi.key} className={`log-row ${lost ? 'lost' : ''} ${err ? 'err' : ''}`} style={{ transform: `translateY(${vi.start}px)` }}>
                  <span className="mono">{clock(r.t)}</span>
                  <span className="r num">{r.round}</span>
                  <span className="r num muted">{r.seq}</span>
                  <span className="r num">{r.ttl || '—'}</span>
                  <span className="mono selectable">{r.from ?? '*'}</span>
                  <span className="st">
                    {statusText[r.status] ?? r.status}
                    {r.rawStatus > 0 && <span className="muted mono"> {r.rawStatus}</span>}
                  </span>
                  <span className="r mono">{r.rttMs != null ? r.rttMs.toFixed(3) : '—'}</span>
                  <span className="r num muted">{r.kernelMs || ''}</span>
                  <span className="r num muted">{r.size || ''}</span>
                  <span className="mono muted">{r.src ?? ''}</span>
                  <span className="muted">{r.proto}</span>
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
