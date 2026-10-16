import { useEffect, useRef, useState } from 'react';
import { AnimatePresence, motion } from 'motion/react';
import { useStore } from './store';
import { TitleBar } from './components/TitleBar';
import { AdapterGate } from './components/AdapterGate';
import { HopTable } from './components/HopTable';
import { RouteMap } from './components/RouteMap';
import { Dock } from './components/Dock';
import { HistoryView } from './components/HistoryView';
import { ContextMenuHost } from './components/ContextMenu';
import { SettingsDialog } from './components/SettingsDialog';
import { Alert, Check, Info } from './components/Icons';
import { duration } from './lib/format';
import './app.css';
import { L, locale } from './i18n';

export function App() {
  const init = useStore((s) => s.init);
  const ready = useStore((s) => s.ready);
  const mapWidth = useStore((s) => s.ui.mapWidth);
  const dockHeight = useStore((s) => s.ui.dockHeight);
  const view = useStore((s) => s.view);

  useEffect(() => {
    void init();
  }, [init]);

  return (
    <div className="app" style={{ ['--map-w' as string]: `${(mapWidth * 100).toFixed(2)}%`, ['--dock-h' as string]: `${dockHeight}px` }}>
      <TitleBar />
      <NetworkBanner />
      {view === 'history' && <HistoryView />}
      <main className="workspace" hidden={view !== 'trace'}>
        <div className="upper">
          <div className="pane-left">
            <HopTable />
            <Insights />
          </div>
          <Splitter axis="x" />
          <div className="pane-right">{ready && <RouteMap />}</div>
        </div>
        <Splitter axis="y" />
        <div className="lower">
          <Dock />
        </div>
      </main>
      <StatusBar />
      <AdapterGate />
      <SettingsDialog />
      <Toast />
      <ContextMenuHost />
    </div>
  );
}

function Splitter({ axis }: { axis: 'x' | 'y' }) {
  const setUi = useStore((s) => s.setUi);
  const ref = useRef<HTMLDivElement>(null);
  const [drag, setDrag] = useState(false);
  const onDown = (e: React.PointerEvent) => {
    const el = ref.current!;
    el.setPointerCapture(e.pointerId);
    setDrag(true);
    const app = el.closest('.app') as HTMLElement;
    const ws = el.closest('.workspace') as HTMLElement;
    const rect = ws.getBoundingClientRect();
    const move = (ev: PointerEvent) => {
      if (axis === 'x') {
        const w = Math.min(0.7, Math.max(0.22, (rect.right - ev.clientX) / rect.width));
        app.style.setProperty('--map-w', `${(w * 100).toFixed(2)}%`);
      } else {
        const h = Math.min(rect.height - 220, Math.max(120, rect.bottom - ev.clientY));
        app.style.setProperty('--dock-h', `${h}px`);
      }
    };
    const up = (ev: PointerEvent) => {
      el.removeEventListener('pointermove', move);
      el.removeEventListener('pointerup', up);
      setDrag(false);
      if (axis === 'x') setUi({ mapWidth: Math.min(0.7, Math.max(0.22, (rect.right - ev.clientX) / rect.width)) });
      else setUi({ dockHeight: Math.round(Math.min(rect.height - 220, Math.max(120, rect.bottom - ev.clientY))) });
    };
    el.addEventListener('pointermove', move);
    el.addEventListener('pointerup', up);
  };
  return <div ref={ref} className={`splitter ${axis} ${drag ? 'drag' : ''}`} onPointerDown={onDown} role="separator" aria-orientation={axis === 'x' ? 'vertical' : 'horizontal'} />;
}

function Insights() {
  const notes = useStore((s) => s.notes);
  const previous = useStore((s) => s.previousRun);
  const vpn = useStore((s) => s.vpn);
  const dest = useStore((s) => s.hops.find((h) => h.isDestination));
  const setView = useStore((s) => s.setView);
  const originNote = useStore((s) => s.routeGeo?.originNote ?? null);
  const setSelected = useStore((s) => s.setSelected);
  const setUi = useStore((s) => s.setUi);
  const top = [...notes].sort((a, b) => rank(b.level) - rank(a.level)).slice(0, 3);
  const nowAvg = vpn ? vpn.avg : dest?.avg ?? null;
  if (top.length === 0 && !previous && !originNote) return null;
  return (
    <div className="insights">
      {originNote && (
        <button className="insight warn" onClick={() => setUi({ dockTab: 'details' })} title={L('Открыть «Сведения», чтобы указать город вручную', 'Open Details to set your city manually')}>
          <Alert size={14} />
          <span>{originNote}</span>
        </button>
      )}
      {previous && (
        <button className="insight info" onClick={() => setView('history')} title={L('Открыть историю цели', 'Open the target history')}>
          <Info size={14} />
          <span>
            {L('В прошлый раз', 'Last time')} ({new Date(previous.startedAt).toLocaleString(locale(), { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })}, {previous.adapterName ?? L('Авто', 'Auto')}{previous.mode === 'vpn' ? ', VPN' : ''}): {previous.avg != null ? `${previous.avg.toFixed(1)} ${L('мс', 'ms')}` : '—'}, {previous.loss ?? 0}% {L('потерь', 'loss')}{nowAvg != null ? ` · ${L('сейчас', 'now')} ${nowAvg.toFixed(1)} ${L('мс', 'ms')}` : ''}
          </span>
        </button>
      )}
      {top.map((n, i) => (
        <button key={`${n.code}-${i}`} className={`insight ${n.level}`} onClick={() => (n.ttl ? setSelected(n.ttl) : setUi({ dockTab: 'details' }))}>
          {n.level === 'ok' ? <Check size={14} /> : n.level === 'info' ? <Info size={14} /> : <Alert size={14} />}
          <span>{n.text}</span>
        </button>
      ))}
      {notes.length > 3 && (
        <button className="insight more" onClick={() => setUi({ dockTab: 'details' })}>
          {L('ещё', 'more')} {notes.length - 3}
        </button>
      )}
    </div>
  );
}
const rank = (l: string) => ({ error: 3, warn: 2, ok: 1, info: 0 })[l] ?? 0;

function NetworkBanner() {
  const session = useStore((s) => s.session);
  const [now, setNow] = useState(Date.now());
  const waiting = session?.state === 'waiting-network';
  useEffect(() => {
    if (!waiting) return;
    const t = window.setInterval(() => setNow(Date.now()), 500);
    return () => window.clearInterval(t);
  }, [waiting]);
  return (
    <AnimatePresence>
      {waiting && (
        <motion.div className="banner" initial={{ height: 0, opacity: 0 }} animate={{ height: 34, opacity: 1 }} exit={{ height: 0, opacity: 0 }} transition={{ duration: 0.2 }}>
          <Alert size={14} />
          <span>
            {L('Сеть недоступна', 'Network unavailable for')} <b className="num">{duration(now - (session?.outageSince ?? now))}</b>.{' '}
            {L('Трассировка на паузе и продолжится сама, как только адаптер снова получит адрес.', 'Tracing is paused and resumes by itself once the adapter gets an address again.')}
          </span>
        </motion.div>
      )}
    </AnimatePresence>
  );
}

const stateText = (s: string) =>
  ({
    resolving: L('Определяем адрес', 'Resolving'),
    running: L('Идёт трассировка', 'Tracing'),
    paused: L('Пауза', 'Paused'),
    'waiting-network': L('Ожидание сети', 'Waiting for network'),
    stopped: L('Остановлено', 'Stopped'),
    finished: L('Завершено', 'Finished'),
    error: L('Ошибка', 'Error'),
  })[s] ?? s;

function StatusBar() {
  const session = useStore((s) => s.session);
  const round = useStore((s) => s.round);
  const elapsed = useStore((s) => s.elapsedMs);
  const geo = useStore((s) => s.geoStatus);
  const info = useStore((s) => s.info);
  const dest = useStore((s) => s.hops.find((h) => h.isDestination));
  const destTtl = useStore((s) => s.destTtl);
  const openSettings = useStore((s) => s.openSettings);
  const updateAvailable = useStore((s) => s.updateAvailable);
  return (
    <footer className="statusbar">
      <span className={`sb-state ${session?.state ?? 'idle'}`}>{session ? stateText(session.state) : L('Готов', 'Ready')}</span>
      {session?.targetIp && (
        <span className="mono sb-route">
          {session.sourceIp ?? '…'} → {session.targetIp}
        </span>
      )}
      {round > 0 && (
        <span className="num">
          {L('раунд', 'round')} {round} · {duration(elapsed)}
        </span>
      )}
      {destTtl && <span className="num">{destTtl} {L('хоп. до цели', 'hops to target')}</span>}
      {dest && dest.avg != null && (
        <span className="num">
          {L('цель', 'target')}: {dest.loss}% {L('потерь', 'loss')}, {dest.avg.toFixed(1)} {L('мс', 'ms')}
        </span>
      )}
      <span className="sb-spacer" />
      {geo && (
        <button className={`sb-link ${geo.db === 'error' ? 'sb-bad' : ''}`} onClick={() => openSettings('geo')} title={L('Геоданные: версии базы, источники', 'Geo data: database versions, sources')}>
          {geo.enabled === false
            ? L('офлайн-база выключена', 'offline database off')
            : geo.db === 'ready'
              ? `${L('гео-база', 'geo DB')} ${geo.dbDate}`
              : geo.db === 'downloading' || geo.db === 'updating'
                ? `${L('гео-база: загрузка', 'geo DB: downloading')} ${Math.round(geo.progress * 100)}%`
                : geo.db === 'error'
                  ? L('гео-база: ошибка загрузки', 'geo DB: download failed')
                  : L('гео-база не загружена', 'geo DB not loaded')}
        </button>
      )}
      {info && !info.isAdmin && <span title={L('Для ICMP-трассировки права администратора не нужны', 'ICMP tracing does not need administrator rights')}>{L('без прав администратора', 'no admin rights')}</span>}
      {info && (
        <button className={`sb-link muted ${updateAvailable ? 'sb-update' : ''}`} onClick={() => openSettings('updates')} title={L('Проверить обновления', 'Check for updates')}>
          v{info.version}
          {updateAvailable && ` · ${L('есть обновление', 'update available')}`}
        </button>
      )}
    </footer>
  );
}

function Toast() {
  const toast = useStore((s) => s.toast);
  return (
    <AnimatePresence>
      {toast && (
        <motion.div
          key={toast.id}
          className={`toast ${toast.level}`}
          initial={{ opacity: 0, y: 8 }}
          animate={{ opacity: 1, y: 0 }}
          exit={{ opacity: 0, y: 8 }}
          transition={{ duration: 0.18, ease: [0.22, 1, 0.36, 1] }}
          role="status"
        >
          {toast.text}
        </motion.div>
      )}
    </AnimatePresence>
  );
}
