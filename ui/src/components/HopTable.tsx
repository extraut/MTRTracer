import { L, locale } from '../i18n';
import { memo, useEffect, useMemo, useRef, useState } from 'react';
import { AnimatePresence, motion } from 'motion/react';
import { request } from '../bridge/client';
import type { Hop, HostInfo, VpnBlock } from '../bridge/protocol';
import { allColumns, useStore, type Column } from '../store';
import { countryName, km, latencyVar, lossLevel, ms, pct, statusText } from '../lib/format';
import { sourceLabel, statusLabel } from '../lib/geodesy';
import { useContextMenu } from './ContextMenu';
import { Sparkline } from './Sparkline';
import { Alert, Columns, Copy, Pin, Play, Route } from './Icons';
import './hops.css';

interface ColDef {
  id: Column;
  label: string;
  title: string;
  width: number | string;
  align: 'left' | 'right';
}

const COLS = (): Record<Column, ColDef> => ({
  host: { id: 'host', label: L('Узел', 'Host'), title: L('Имя и IP ответившего роутера', 'Name and IP of the router that answered'), width: 'minmax(220px, 1.6fr)', align: 'left' },
  asn: { id: 'asn', label: L('Сеть · место', 'Network · location'), title: L('Автономная система и геолокация', 'Autonomous system and geolocation'), width: 'minmax(150px, 1fr)', align: 'left' },
  loss: { id: 'loss', label: 'Loss%', title: L('Процент потерянных пакетов', 'Share of lost packets'), width: 62, align: 'right' },
  sent: { id: 'sent', label: 'Sent', title: L('Отправлено пакетов', 'Packets sent'), width: 54, align: 'right' },
  recv: { id: 'recv', label: 'Recv', title: L('Получено ответов', 'Replies received'), width: 54, align: 'right' },
  last: { id: 'last', label: 'Last', title: L('Последний RTT, мс', 'Last RTT, ms'), width: 62, align: 'right' },
  avg: { id: 'avg', label: 'Avg', title: L('Средний RTT, мс', 'Average RTT, ms'), width: 66, align: 'right' },
  best: { id: 'best', label: 'Best', title: L('Минимальный RTT, мс', 'Minimum RTT, ms'), width: 62, align: 'right' },
  worst: { id: 'worst', label: 'Worst', title: L('Максимальный RTT, мс', 'Maximum RTT, ms'), width: 62, align: 'right' },
  stdev: { id: 'stdev', label: 'StDev', title: L('Стандартное отклонение RTT, мс', 'Standard deviation of RTT, ms'), width: 62, align: 'right' },
  gmean: { id: 'gmean', label: 'Gmean', title: L('Среднее геометрическое RTT, мс', 'Geometric mean of RTT, ms'), width: 62, align: 'right' },
  jitter: { id: 'jitter', label: 'Jttr', title: L('Текущий джиттер |RTTn − RTTn−1|, мс', 'Current jitter |RTTn − RTTn−1|, ms'), width: 58, align: 'right' },
  javg: { id: 'javg', label: 'Javg', title: L('Средний джиттер, мс', 'Average jitter, ms'), width: 58, align: 'right' },
  jmax: { id: 'jmax', label: 'Jmax', title: L('Максимальный джиттер, мс', 'Maximum jitter, ms'), width: 58, align: 'right' },
  jint: { id: 'jint', label: 'Jint', title: L('Межпакетный джиттер по RFC 3550, мс', 'Interarrival jitter per RFC 3550, ms'), width: 58, align: 'right' },
  history: { id: 'history', label: L('Последние 60', 'Last 60'), title: L('История RTT; красное: потеря', 'RTT history; red: loss'), width: 136, align: 'left' },
});

export function HopTable() {
  const hops = useStore((s) => s.hops);
  const session = useStore((s) => s.session);
  const columns = useStore((s) => s.ui.columns);
  const tcp = useStore((s) => s.tcp);
  const vpn = useStore((s) => s.vpn);
  const cols = useMemo(() => allColumns.filter((c) => columns.includes(c)).map((c) => COLS()[c]), [columns]);
  const template = useMemo(() => ['34px', ...cols.map((c) => (typeof c.width === 'number' ? `${c.width}px` : c.width))].join(' '), [cols]);
  const minWidth = useMemo(
    () => 64 + cols.reduce((s, c) => s + (typeof c.width === 'number' ? c.width : Number(/minmax\((\d+)px/.exec(c.width)?.[1] ?? 120)), 0),
    [cols],
  );
  const scaleMax = useMemo(() => {
    const all = hops.flatMap((h) => h.history.slice(-60)).filter((v): v is number => v != null);
    if (!all.length) return 10;
    all.sort((a, b) => a - b);
    return Math.max(10, all[Math.floor(all.length * 0.98)] * 1.1);
  }, [hops]);

  const scroller = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = scroller.current;
    if (!el) return;
    const ro = new ResizeObserver(() => el.style.setProperty('--hops-w', `${el.clientWidth}px`));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const empty = !session || (hops.length === 0 && session.state !== 'resolving' && session.state !== 'running');

  return (
    <div className="hops" ref={scroller} style={{ ['--cols' as string]: template }}>
      <div className="hops-inner" style={{ minWidth }}>
      <div className="hops-head" role="row">
        <div className="hc right" title={L('Номер хопа (TTL)', 'Hop number (TTL)')}>#</div>
        {cols.map((c) => (
          <div key={c.id} className={`hc ${c.align}`} title={c.title}>
            {c.label}
          </div>
        ))}
        <ColumnPicker />
      </div>
      <div className="hops-body" role="table" aria-label={L('Хопы маршрута', 'Route hops')}>
        {empty ? (
          <EmptyState />
        ) : hops.length === 0 ? (
          <Skeleton cols={cols.length} />
        ) : (
          <>
            {vpn && (
              <div className="seg-row">
                <span className="seg-tag">{L('Через', 'Via')} {vpn.carrier ?? L('физический адаптер', 'physical adapter')}</span>
                <span>
                  {L('настоящие хопы до сервера VPN', 'real hops to the VPN server')} <span className="mono">{vpn.server}</span>
                  {vpn.process ? ` (${vpn.process})` : ''}
                </span>
              </div>
            )}
            {hops.map((h) => (
              <HopRow key={h.ttl} hop={h} cols={cols} scaleMax={scaleMax} />
            ))}
            {vpn && <VpnRows vpn={vpn} cols={cols} scaleMax={scaleMax} />}
            {tcp && (
              <div className="hop-row tcp" role="row">
                <div className="cell right ttl">TCP</div>
                {cols.map((c) => (
                  <div key={c.id} className={`cell ${c.align}`}>
                    {c.id === 'host' ? (
                      <span className="host-line">
                        <span className="host-name">{L('TCP-пинг', 'TCP ping')} :{tcp.port}</span>
                        <span className="mono muted">{tcp.status === 'refused' ? L('порт закрыт (RST), но узел отвечает', 'port closed (RST), but the host answers') : tcp.status}</span>
                      </span>
                    ) : c.id === 'loss' ? (
                      <span className={`loss ${lossLevel(tcp.loss)}`}>{pct(tcp.loss)}</span>
                    ) : c.id === 'sent' ? (
                      tcp.sent
                    ) : c.id === 'recv' ? (
                      tcp.recv
                    ) : c.id === 'last' ? (
                      <Lat v={tcp.last} />
                    ) : c.id === 'avg' ? (
                      <Lat v={tcp.avg} dot />
                    ) : c.id === 'best' ? (
                      ms(tcp.best)
                    ) : c.id === 'worst' ? (
                      ms(tcp.worst)
                    ) : null}
                  </div>
                ))}
              </div>
            )}
          </>
        )}
      </div>
      </div>
    </div>
  );
}

function Lat({ v, dot }: { v: number | null; dot?: boolean }) {
  return (
    <span className="lat">
      {dot && v != null && <span className="lat-dot" style={{ background: latencyVar(v) }} />}
      {ms(v)}
    </span>
  );
}

const HopRow = memo(function HopRow({ hop, cols, scaleMax }: { hop: Hop; cols: ColDef[]; scaleMax: number }) {
  const info = useStore((s) => (hop.primary ? s.hosts[hop.primary] : undefined));
  const hover = useStore((s) => s.hoverTtl === hop.ttl);
  const selected = useStore((s) => s.selectedTtl === hop.ttl);
  const setHover = useStore((s) => s.setHover);
  const setSelected = useStore((s) => s.setSelected);
  const openMenu = useContextMenu((s) => s.open);
  const silent = hop.recv === 0 && hop.sent > 0;
  const pulseKey = hop.recv;
  const onContext = (e: React.MouseEvent) => {
    if (!hop.primary) return;
    const st = useStore.getState();
    const copy = (text: string) => {
      void request('clipboard.copy', { text });
      st.notify(`${L('Скопировано', 'Copied')}: ${text}`);
    };
    openMenu(e, [
      { title: `${L('Хоп', 'Hop')} ${hop.ttl} · ${info?.hostname ?? hop.primary}` },
      { label: L('Трассировать до узла', 'Trace to this host'), icon: <Play size={11} />, hint: hop.primary, disabled: hop.isDestination, onSelect: () => st.start(hop.primary!) },
      { label: L('Показать подробности', 'Show details'), onSelect: () => st.selectedTtl !== hop.ttl && st.setSelected(hop.ttl) },
      { separator: true },
      { label: L('Копировать IP', 'Copy IP'), icon: <Copy size={13} />, onSelect: () => copy(hop.primary!) },
      { label: L('Копировать имя', 'Copy name'), icon: <Copy size={13} />, disabled: !info?.hostname, onSelect: () => copy(info!.hostname!) },
      { label: L('Копировать строку', 'Copy row'), icon: <Copy size={13} />, onSelect: () => copy(rowText(hop, info)) },
    ]);
  };

  return (
    <motion.div
      className={`hop-wrap ${selected ? 'open' : ''}`}
      initial={{ opacity: 0, y: 4 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.22, ease: [0.22, 1, 0.36, 1] }}
    >
      <div
        className={`hop-row ${hover ? 'hover' : ''} ${selected ? 'sel' : ''} ${hop.isDestination ? 'dest' : ''} ${silent ? 'silent' : ''}`}
        role="row"
        onMouseEnter={() => setHover(hop.ttl)}
        onMouseLeave={() => setHover(null)}
        onClick={() => setSelected(hop.ttl)}
        onContextMenu={onContext}
      >
        <div className="cell right ttl num">
          {hop.isDestination ? <span className="ttl-dest">{hop.ttl}</span> : hop.ttl}
        </div>
        {cols.map((c) => (
          <div key={c.id} className={`cell ${c.align} ${c.id}`}>
            <CellValue col={c.id} hop={hop} info={info} scaleMax={scaleMax} pulseKey={pulseKey} />
          </div>
        ))}
      </div>
      <AnimatePresence initial={false}>{selected && <HopDetails hop={hop} info={info} />}</AnimatePresence>
    </motion.div>
  );
});

function CellValue({ col, hop, info, scaleMax, pulseKey }: { col: Column; hop: Hop; info?: HostInfo; scaleMax: number; pulseKey: number }) {
  switch (col) {
    case 'host': {
      if (!hop.primary)
        return (
          <span className="host-line">
            <span className="host-name muted">{hop.sent === 0 ? L('ожидание…', 'waiting…') : L('Нет ответа', 'No reply')}</span>
            <span className="mono muted">* * *</span>
          </span>
        );
      const name = info?.hostname;
      return (
        <span className="host-line" title={name ? `${name}\n${hop.primary}` : hop.primary}>
          {name ? <span className="host-name">{name}</span> : null}
          <span className={`mono ${name ? 'host-ip' : 'host-name'}`}>{hop.primary}</span>
          {hop.responders.length > 1 && <span className="badge">+{hop.responders.length - 1}</span>}
          {isError(hop.status) && <span className="badge bad">{statusText[hop.status] ?? hop.status}</span>}
          <span key={pulseKey} className="host-pulse" />
        </span>
      );
    }
    case 'asn':
      return <NetCell info={info} hop={hop} />;
    case 'loss':
      return <span className={`loss ${lossLevel(hop.loss)}`}>{hop.sent ? pct(hop.loss) : '—'}</span>;
    case 'sent':
      return <span className="num">{hop.sent}</span>;
    case 'recv':
      return <span className="num">{hop.recv}</span>;
    case 'last':
      return <Lat v={hop.last} />;
    case 'avg':
      return <Lat v={hop.avg} dot />;
    case 'best':
      return <span className="num">{ms(hop.best)}</span>;
    case 'worst':
      return <span className="num">{ms(hop.worst)}</span>;
    case 'stdev':
      return <span className="num">{ms(hop.stDev)}</span>;
    case 'gmean':
      return <span className="num">{ms(hop.gMean)}</span>;
    case 'jitter':
      return <span className="num">{ms(hop.jitter)}</span>;
    case 'javg':
      return <span className="num">{ms(hop.jAvg)}</span>;
    case 'jmax':
      return <span className="num">{ms(hop.jMax)}</span>;
    case 'jint':
      return <span className="num">{ms(hop.jInt)}</span>;
    case 'history':
      return <Sparkline values={hop.history} width={124} height={18} slots={60} scaleMax={scaleMax} />;
  }
}

const isError = (s: string) => !['ok', 'ttl-expired', 'timeout', 'pending'].includes(s);

const kindText = (k: string) =>
  ({
    private: L('локальная сеть', 'local network'),
    cgnat: L('CGNAT провайдера', 'provider CGNAT'),
    'link-local': 'link-local',
    loopback: 'loopback',
    ula: L('частный IPv6', 'private IPv6'),
    multicast: 'multicast',
    reserved: L('зарезервирован', 'reserved'),
  })[k] ?? k;

function useHopGeo(ttl: number) {
  return useStore((s) => s.routeGeo?.hops.find((h) => h.ttl === ttl) ?? null);
}

function NetCell({ info, hop }: { info?: HostInfo; hop: Hop }) {
  const g = useHopGeo(hop.ttl);
  if (!hop.primary) return null;
  if (!info) return <span className="muted">…</span>;
  if (info.kind !== 'public') return <span className="muted">{kindText(info.kind)}</span>;
  const place = g && g.lat != null ? [g.city, g.countryCode].filter(Boolean).join(', ') : null;
  return (
    <span className="net-line" title={[info.asn ? `AS${info.asn} ${info.asName ?? ''}` : null, g?.reason].filter(Boolean).join('\n')}>
      <span className="net-as">
        {info.asn ? <span className="mono">AS{info.asn}</span> : null} {info.asName ?? info.isp ?? ''}
      </span>
      {g?.status === 'anycast' ? (
        <span className="net-place anycast">anycast{place ? `, ${L('ближайший', 'nearest')}: ${place}` : ''}</span>
      ) : g?.status === 'unresolved' ? (
        <span className="net-place suspect" title={g.reason ?? undefined}>
          {place ? `? ${place} · ${L('не сходится с RTT', 'does not match RTT')}` : L('нет данных о месте', 'no location data')}
        </span>
      ) : g?.status === 'pending' || !g ? (
        <span className="net-place">{L('определяется…', 'resolving…')}</span>
      ) : (
        place && (
          <span className={`net-place ${g.status}`}>
            {g.status === 'single' ? '≈ ' : ''}
            {place}
          </span>
        )
      )}
    </span>
  );
}

function age(t: number): string {
  if (!t) return '';
  const d = Date.now() - t;
  if (d < 60_000) return L('только что', 'just now');
  if (d < 3_600_000) return L(`${Math.round(d / 60_000)} мин назад`, `${Math.round(d / 60_000)} min ago`);
  if (d < 86_400_000) return L(`${Math.round(d / 3_600_000)} ч назад`, `${Math.round(d / 3_600_000)} h ago`);
  return new Date(t).toLocaleDateString(locale());
}

function HopDetails({ hop, info }: { hop: Hop; info?: HostInfo }) {
  const g = useHopGeo(hop.ttl);
  const hosts = useStore((s) => s.hosts);
  const start = useStore((s) => s.start);
  const notify = useStore((s) => s.notify);
  const copy = (text: string) => {
    void request('clipboard.copy', { text });
    notify(`${L('Скопировано', 'Copied')}: ${text}`);
  };
  const place =
    g && g.lat != null && g.status !== 'local'
      ? `${[g.city, countryName(g.countryCode)].filter(Boolean).join(', ') || L('координаты', 'coordinates')} · ${g.lat.toFixed(3)}, ${g.lon!.toFixed(3)}`
      : null;
  return (
    <motion.div
      className="hop-details"
      initial={{ height: 0, opacity: 0 }}
      animate={{ height: 'auto', opacity: 1 }}
      exit={{ height: 0, opacity: 0 }}
      transition={{ duration: 0.2, ease: [0.22, 1, 0.36, 1] }}
    >
      <div className="hd-inner">
        <div className="hd-chart">
          <div className="hd-label">{L('RTT по пакетам, последние 120', 'RTT per packet, last 120')}</div>
          <Sparkline values={hop.history} width={360} height={56} slots={120} />
          <div className="hd-stats num">
            <span>min {ms(hop.best)}</span>
            <span>avg {ms(hop.avg)}</span>
            <span>max {ms(hop.worst)}</span>
            <span>σ {ms(hop.stDev)}</span>
            <span>Jint {ms(hop.jInt)}</span>
            <span>Jmax {ms(hop.jMax)}</span>
          </div>
          <div className="hd-note">{L('RTT по высокоточному таймеру (микросекунды, включает ~0.1 мс накладных расходов API). На Windows в логе пакетов есть и время ядра в мс.', 'RTT from a high-resolution timer (microseconds, includes ~0.1 ms of API overhead). On Windows the packet log also has the kernel time in ms.')}</div>
        </div>
        <dl className="hd-facts">
          <Fact k="IP" v={hop.primary} mono />
          <Fact k={L('Имя', 'Name')} v={info?.hostname ?? (info?.hostnameDone ? L('нет PTR-записи', 'no PTR record') : hop.primary ? L('определяется…', 'resolving…') : null)} mono />
          <Fact k="AS" v={info?.asn ? `AS${info.asn} ${info.asName ?? ''}${info.asSource ? ` · ${info.asSource}` : ''}` : null} />
          <Fact k={L('Префикс', 'Prefix')} v={info?.prefix ? `${info.prefix}${info.registry ? ` · ${info.registry.toUpperCase()}` : ''}` : null} mono />
          <Fact k={L('Провайдер', 'Provider')} v={info?.isp && info.isp !== info.asName ? `${info.isp}${info.org && info.org !== info.isp ? ` (${info.org})` : ''}` : info?.org ?? null} />
          <Fact k={L('Место', 'Location')} v={place ?? (g ? statusLabel[g.status] : null)} warn={g?.status === 'unresolved'} />
          <Fact k={L('Решение', 'Decision')} v={g?.reason ?? null} warn={g?.status === 'unresolved'} />
          {g?.maxDistanceKm != null && <Fact k={L('Физ. предел', 'Physical limit')} v={L(`не дальше ${km(g.maxDistanceKm)} от вас (по минимальному RTT)`, `no farther than ${km(g.maxDistanceKm)} from you (by minimum RTT)`)} />}
          <Fact k="ICMP" v={`${statusText[hop.status] ?? hop.status}${hop.consecutiveLoss > 1 ? `, ${hop.consecutiveLoss} ${L('потерь подряд', 'lost in a row')}` : ''}`} />
        </dl>
        {g && g.verdicts.length > 0 && (
          <div className="hd-sources">
            <div className="hd-label">{L('Что говорят источники геолокации', 'What the geolocation sources say')}</div>
            {g.verdicts.map((v) => (
              <div key={v.source} className={`src-row ${v.accepted ? 'ok' : 'no'}`}>
                <span className="src-mark">{v.accepted ? '✓' : '✕'}</span>
                <span className="src-name">{sourceLabel[v.source] ?? v.source}</span>
                <span className="src-place">
                  {[v.city, v.countryCode].filter(Boolean).join(', ') || `${v.lat.toFixed(2)}, ${v.lon.toFixed(2)}`}
                  {v.level !== 'city' && <span className="muted"> ({L('страна', 'country')})</span>}
                </span>
                <span className="src-why">{v.reason ?? (v.accepted ? L('согласуется с RTT', 'consistent with RTT') : '')}</span>
                <span className="src-age muted">{age(v.fetchedAt)}</span>
              </div>
            ))}
          </div>
        )}
        {hop.responders.length > 1 && (
          <div className="hd-ecmp">
            <div className="hd-label">{L('Несколько узлов на этом хопе: балансировка (ECMP) или смена маршрута', 'Several routers on this hop: load balancing (ECMP) or a route change')}</div>
            {hop.responders.map((x) => (
              <div key={x.ip} className="ecmp-row">
                <span className="ecmp-bar" style={{ width: `${Math.max(2, x.share)}%` }} />
                <span className="mono">{x.ip}</span>
                <span className="muted">{hosts[x.ip]?.hostname ?? ''}</span>
                <span className="num ecmp-n">
                  {x.share}% · {ms(x.avg)} {L('мс', 'ms')}
                </span>
              </div>
            ))}
          </div>
        )}
        <div className="hd-actions">
          {hop.primary && (
            <>
              <button className="btn sm" onClick={() => copy(hop.primary!)}>
                <Copy size={13} /> IP
              </button>
              {info?.hostname && (
                <button className="btn sm" onClick={() => copy(info.hostname!)}>
                  <Copy size={13} /> {L('Имя', 'Name')}
                </button>
              )}
              {!hop.isDestination && (
                <button className="btn sm" onClick={() => start(hop.primary!)} title={L('Новая трассировка до этого узла', 'New trace to this host')}>
                  <Play size={12} /> {L('Трассировать до узла', 'Trace to this host')}
                </button>
              )}
            </>
          )}
        </div>
      </div>
    </motion.div>
  );
}

function rowText(hop: Hop, info?: HostInfo): string {
  return `${hop.ttl}\t${info?.hostname ?? ''}\t${hop.primary ?? '*'}\tloss ${hop.loss}%\tsent ${hop.sent}\tbest ${ms(hop.best)}\tavg ${ms(hop.avg)}\tworst ${ms(hop.worst)}\tlast ${ms(hop.last)}`;
}

/** VPN mode: the tunnel boundary and the destination measured through it. */
function VpnRows({ vpn, cols, scaleMax }: { vpn: VpnBlock; cols: ColDef[]; scaleMax: number }) {
  const exit = vpn.exit;
  const t = vpn.targetGeo;
  return (
    <>
      <div className="seg-row tunnel">
        <span className="seg-tag accent">{L('Туннель', 'Tunnel')}</span>
        <span>
          {L('выход', 'exit')}: <span className="mono">{exit?.ip ?? '…'}</span>
          {exit?.asOrg ? ` · ${exit.asOrg}` : ''}
          {exit?.city ? ` · ${exit.city}${exit.country ? `, ${exit.country}` : ''}` : ''}
          {exit?.colo ? ` · Cloudflare ${exit.colo}` : ''}
        </span>
        {vpn.legMs != null && (
          <span className="seg-leg" title={L('Сквозной минимальный RTT до цели минус минимальный RTT до сервера VPN', 'End-to-end minimum RTT to the target minus minimum RTT to the VPN server')}>
            {L('сервер → цель', 'server → target')} ≈ <b className="num">{ms(vpn.legMs)} {L('мс', 'ms')}</b>
          </span>
        )}
      </div>
      <div className="hop-row dest vpn-target" role="row">
        <div className="cell right ttl">
          <span className="ttl-dest">⇢</span>
        </div>
        {cols.map((c) => (
          <div key={c.id} className={`cell ${c.align} ${c.id}`}>
            {c.id === 'host' ? (
              <span className="host-line" title={L('Время ответа на HTTP-запрос через туннель (keep-alive): настоящая сквозная задержка', 'Response time of an HTTP request through the tunnel (keep-alive): the real end-to-end latency')}>
                <span className="host-name">{vpn.target}</span>
                <span className="mono host-ip">{vpn.targetIp ?? ''}</span>
                <span className="badge accent">
                  {vpn.method.toUpperCase()} :{vpn.port}
                </span>
              </span>
            ) : c.id === 'asn' ? (
              <span className="net-line">
                <span className="net-as">{L('через туннель', 'through the tunnel')}{vpn.status && !['https', 'http'].includes(vpn.status) ? ` · ${vpn.status}` : ''}</span>
                {t && t.lat != null && <span className={`net-place ${t.status}`}>{[t.city, t.countryCode].filter(Boolean).join(', ')}</span>}
              </span>
            ) : c.id === 'loss' ? (
              <span className={`loss ${lossLevel(vpn.loss)}`}>{vpn.sent ? pct(vpn.loss) : '—'}</span>
            ) : c.id === 'sent' ? (
              vpn.sent
            ) : c.id === 'recv' ? (
              vpn.recv
            ) : c.id === 'last' ? (
              <Lat v={vpn.last} />
            ) : c.id === 'avg' ? (
              <Lat v={vpn.avg} dot />
            ) : c.id === 'best' ? (
              ms(vpn.best)
            ) : c.id === 'worst' ? (
              ms(vpn.worst)
            ) : c.id === 'javg' ? (
              ms(vpn.jAvg)
            ) : c.id === 'history' ? (
              <Sparkline values={vpn.history} width={124} height={18} slots={60} scaleMax={scaleMax} />
            ) : null}
          </div>
        ))}
      </div>
      {vpn.connectFaked && (
        <div className="seg-note">
          {L('TCP-соединение «устанавливается» за доли миллисекунды: его подтверждает сам туннель. Поэтому задержка меряется по ответу на HTTP-запрос, он действительно доходит до цели.', 'The TCP connection "opens" in a fraction of a millisecond: the tunnel itself acknowledges it. That is why latency is measured by the HTTP response, which really reaches the target.')}
          {vpn.setupMs != null && ` ${L('Установка', 'Setup')} ${vpn.method.toUpperCase()}: ${ms(vpn.setupMs)} ${L('мс', 'ms')}.`}
        </div>
      )}
    </>
  );
}

function Fact({ k, v, mono, warn }: { k: string; v: string | null | undefined; mono?: boolean; warn?: boolean }) {
  return (
    <div className="fact">
      <dt>{k}</dt>
      <dd className={`${mono ? 'mono' : ''} ${warn ? 'warn' : ''} selectable`}>{v || <span className="muted">—</span>}</dd>
    </div>
  );
}

function ColumnPicker() {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const columns = useStore((s) => s.ui.columns);
  const setUi = useStore((s) => s.setUi);
  useEffect(() => {
    if (!open) return;
    const h = (e: MouseEvent) => ref.current && !ref.current.contains(e.target as Node) && setOpen(false);
    window.addEventListener('mousedown', h);
    return () => window.removeEventListener('mousedown', h);
  }, [open]);
  return (
    <div className="colpick" ref={ref}>
      <button className={`btn ghost sm icon ${open ? 'active' : ''}`} onClick={() => setOpen(!open)} title={L('Колонки', 'Columns')} aria-label={L('Выбрать колонки', 'Choose columns')}>
        <Columns size={14} />
      </button>
      {open && (
        <div className="popover colpick-pop">
          {allColumns
            .filter((c) => c !== 'host')
            .map((c) => {
              const on = columns.includes(c);
              return (
                <button
                  key={c}
                  className="menu-item"
                  onClick={() => setUi({ columns: on ? columns.filter((x) => x !== c) : [...columns, c] })}
                  role="menuitemcheckbox"
                  aria-checked={on}
                >
                  <span className={`check ${on ? 'on' : ''}`} />
                  <span>{COLS()[c].label}</span>
                  <span className="hint">{COLS()[c].title}</span>
                </button>
              );
            })}
        </div>
      )}
    </div>
  );
}

function Skeleton({ cols }: { cols: number }) {
  return (
    <div className="hops-skel">
      {Array.from({ length: 6 }, (_, i) => (
        <div key={i} className="hop-row skel" style={{ opacity: 1 - i * 0.14 }}>
          <div className="cell" />
          {Array.from({ length: cols }, (_, j) => (
            <div key={j} className="cell">
              <span className="skel-bar" />
            </div>
          ))}
        </div>
      ))}
    </div>
  );
}

const presets = ['1.1.1.1', '8.8.8.8', 'google.com', 'ya.ru', 'cloudflare.com'];

function EmptyState() {
  const session = useStore((s) => s.session);
  const recent = useStore((s) => s.recent);
  const start = useStore((s) => s.start);
  const openGate = useStore((s) => s.openAdapterGate);
  const options = useStore((s) => s.options);
  const setOptions = useStore((s) => s.setOptions);
  const chips = [...new Set([...recent.slice(0, 5), ...presets])].slice(0, 8);

  if (session?.state === 'error') {
    const code = session.errorCode;
    return (
      <div className="hops-empty error">
        <Alert size={22} />
        <h2>{L('Трассировка не запустилась', 'The trace did not start')}</h2>
        <p className="selectable">{session.error}</p>
        <div className="hops-empty-actions">
          {(code === 'no-address' || code === 'adapter-down' || code === 'adapter-missing') && (
            <button className="btn" onClick={() => openGate(true)}>
              {L('Выбрать другой адаптер', 'Choose another adapter')}
            </button>
          )}
          {code === 'no-address' && options.ipVersion !== '4' && (
            <button
              className="btn"
              onClick={() => {
                setOptions({ ipVersion: '4' });
                setTimeout(() => start(session.input), 50);
              }}
            >
              {L('Повторить по IPv4', 'Retry over IPv4')}
            </button>
          )}
          <button className="btn primary" onClick={() => start(session.input)}>
            {L('Повторить', 'Retry')}
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="hops-empty">
      <Route size={22} />
      <h2>{session ? L('Трассировка остановлена', 'Trace stopped') : L('Куда проложить маршрут?', 'Where should the route go?')}</h2>
      <p>{L('Введите IP-адрес, домен или ссылку в строке сверху и нажмите Enter. Каждый узел на пути появится строкой с потерями, задержками и местом на карте.', 'Type an IP address, a domain or a link in the field above and press Enter. Every router on the path appears as a row with loss, latency and a place on the map.')}</p>
      <div className="chips">
        {chips.map((c) => (
          <button key={c} className="chip mono" onClick={() => start(c)}>
            {c}
          </button>
        ))}
      </div>
      <div className="hops-empty-tip">
        <Pin size={13} /> {L('Подсказка: в WinMTR потери на промежуточном хопе часто пугают зря. Здесь под таблицей появится разбор, где проблема настоящая.', 'Tip: in WinMTR, loss on an intermediate hop often scares people for nothing. Here an analysis below the table shows where the problem is real.')}
      </div>
    </div>
  );
}
