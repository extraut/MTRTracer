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
import { Alert, Check, Info } from './components/Icons';
import { duration } from './lib/format';
import './app.css';

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
        <button className="insight warn" onClick={() => setUi({ dockTab: 'details' })} title="Открыть «Сведения», чтобы указать город вручную">
          <Alert size={14} />
          <span>{originNote}</span>
        </button>
      )}
      {previous && (
        <button className="insight info" onClick={() => setView('history')} title="Открыть историю цели">
          <Info size={14} />
          <span>
            В прошлый раз ({new Date(previous.startedAt).toLocaleString('ru-RU', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })}, {previous.adapterName ?? 'Авто'}{previous.mode === 'vpn' ? ', VPN' : ''}): {previous.avg != null ? `${previous.avg.toFixed(1)} мс` : '—'}, {previous.loss ?? 0}% потерь{nowAvg != null ? ` · сейчас ${nowAvg.toFixed(1)} мс` : ''}
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
          ещё {notes.length - 3}
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
            Сеть недоступна <b className="num">{duration(now - (session?.outageSince ?? now))}</b>. Трассировка на паузе и продолжится сама, как только адаптер снова получит адрес.
          </span>
        </motion.div>
      )}
    </AnimatePresence>
  );
}

const stateText: Record<string, string> = {
  resolving: 'Определяем адрес',
  running: 'Идёт трассировка',
  paused: 'Пауза',
  'waiting-network': 'Ожидание сети',
  stopped: 'Остановлено',
  finished: 'Завершено',
  error: 'Ошибка',
};

function StatusBar() {
  const session = useStore((s) => s.session);
  const round = useStore((s) => s.round);
  const elapsed = useStore((s) => s.elapsedMs);
  const geo = useStore((s) => s.geoStatus);
  const info = useStore((s) => s.info);
  const dest = useStore((s) => s.hops.find((h) => h.isDestination));
  const destTtl = useStore((s) => s.destTtl);
  return (
    <footer className="statusbar">
      <span className={`sb-state ${session?.state ?? 'idle'}`}>{session ? stateText[session.state] : 'Готов'}</span>
      {session?.targetIp && (
        <span className="mono sb-route">
          {session.sourceIp ?? '…'} → {session.targetIp}
        </span>
      )}
      {round > 0 && (
        <span className="num">
          раунд {round} · {duration(elapsed)}
        </span>
      )}
      {destTtl && <span className="num">{destTtl} хоп. до цели</span>}
      {dest && dest.avg != null && (
        <span className="num">
          цель: {dest.loss}% потерь, {dest.avg.toFixed(1)} мс
        </span>
      )}
      <span className="sb-spacer" />
      {geo && (
        <span className={geo.db === 'error' ? 'sb-bad' : ''}>
          {geo.db === 'ready'
            ? `гео-база ${geo.dbDate}`
            : geo.db === 'downloading' || geo.db === 'updating'
              ? `гео-база: загрузка ${Math.round(geo.progress * 100)}%`
              : geo.db === 'error'
                ? 'гео-база: ошибка загрузки'
                : 'гео-база не загружена'}
        </span>
      )}
      {info && !info.isAdmin && <span title="Для ICMP-трассировки права администратора не нужны">без прав администратора</span>}
      {info && <span className="muted">v{info.version}</span>}
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
