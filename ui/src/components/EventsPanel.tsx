import { L } from '../i18n';
import { useEffect, useRef } from 'react';
import { useStore } from '../store';
import { clock } from '../lib/format';
import { Alert, Check, Info } from './Icons';

export function EventsPanel() {
  const events = useStore((s) => s.events);
  const setSelected = useStore((s) => s.setSelected);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = ref.current;
    if (el && el.scrollHeight - el.scrollTop - el.clientHeight < 80) el.scrollTop = el.scrollHeight;
  }, [events.length]);

  return (
    <div className="panel events" ref={ref}>
      {events.length === 0 ? (
        <div className="empty">{L('Смена маршрута, серии потерь, обрывы сети, переход на TCP-пинг и другие события сессии появятся здесь с точным временем.', 'Route changes, loss bursts, network drops, the switch to TCP ping and other session events show up here with exact times.')}</div>
      ) : (
        events.map((e, i) => (
          <div key={i} className={`ev ${e.level}`}>
            <span className="ev-icon">{e.level === 'error' ? <Alert size={14} /> : e.level === 'warn' ? <Alert size={14} /> : e.code === 'destination' || e.code === 'recovered' || e.code === 'network-up' ? <Check size={14} /> : <Info size={14} />}</span>
            <span className="mono ev-t">{clock(e.t)}</span>
            <span className="ev-msg selectable">{e.message}</span>
            {e.ttl != null && e.ttl > 0 && (
              <button className="badge ev-ttl" onClick={() => setSelected(e.ttl)}>
                {L('хоп', 'hop')} {e.ttl}
              </button>
            )}
          </div>
        ))
      )}
    </div>
  );
}
