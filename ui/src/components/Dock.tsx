import { lazy, Suspense } from 'react';
import { motion } from 'motion/react';
import { useStore, type DockTab } from '../store';
import { ProbeLog } from './ProbeLog';
import { EventsPanel } from './EventsPanel';
import { DetailsPanel } from './DetailsPanel';
import './dock.css';

const LatencyChart = lazy(() => import('./LatencyChart').then((m) => ({ default: m.LatencyChart })));
const ConnectionsPanel = lazy(() => import('./ConnectionsPanel').then((m) => ({ default: m.ConnectionsPanel })));

export function Dock() {
  const tab = useStore((s) => s.ui.dockTab);
  const setUi = useStore((s) => s.setUi);
  const events = useStore((s) => s.events);
  const warnCount = events.filter((e) => e.level !== 'info').length;
  const conns = useStore((s) => s.connections.length);

  const tabs: [DockTab, string, React.ReactNode?][] = [
    ['probes', 'Пакеты'],
    ['events', 'События', warnCount > 0 ? <span className="tab-badge">{warnCount}</span> : events.length > 0 ? <span className="tab-count">{events.length}</span> : null],
    ['chart', 'Задержка во времени'],
    ['connections', 'Соединения процессов', conns > 0 && tab === 'connections' ? <span className="tab-count">{conns}</span> : null],
    ['details', 'Сведения'],
  ];

  return (
    <section className="dock">
      <nav className="dock-tabs" role="tablist">
        {tabs.map(([id, label, extra]) => (
          <button key={id} role="tab" aria-selected={tab === id} className={`dock-tab ${tab === id ? 'on' : ''}`} onClick={() => setUi({ dockTab: id })}>
            {label}
            {extra}
            {tab === id && <motion.span layoutId="dock-underline" className="dock-underline" transition={{ duration: 0.2, ease: [0.22, 1, 0.36, 1] }} />}
          </button>
        ))}
      </nav>
      <div className="dock-body" role="tabpanel">
        <Suspense fallback={<div className="empty">Загрузка…</div>}>
          {tab === 'probes' && <ProbeLog />}
          {tab === 'events' && <EventsPanel />}
          {tab === 'chart' && <LatencyChart />}
          {tab === 'connections' && <ConnectionsPanel />}
          {tab === 'details' && <DetailsPanel />}
        </Suspense>
      </div>
    </section>
  );
}
