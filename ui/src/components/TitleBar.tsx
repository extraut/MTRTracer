import { useEffect, useRef, useState } from 'react';
import { AnimatePresence, motion } from 'motion/react';
import { hostKind, request } from '../bridge/client';
import type { ExportFormat, TraceOptions } from '../bridge/protocol';
import { useStore } from '../store';
import { AdapterIcon, Chevron, Close, Export, Folder, Maximize, Minimize, Pause, Play, Restart, Restore, Sliders, Stop } from './Icons';
import './titlebar.css';

function useOutside(ref: React.RefObject<HTMLElement | null>, onOut: () => void, active: boolean) {
  useEffect(() => {
    if (!active) return;
    const h = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) onOut();
    };
    const k = (e: KeyboardEvent) => e.key === 'Escape' && onOut();
    window.addEventListener('mousedown', h);
    window.addEventListener('keydown', k);
    return () => {
      window.removeEventListener('mousedown', h);
      window.removeEventListener('keydown', k);
    };
  }, [ref, onOut, active]);
}

const pop = {
  initial: { opacity: 0, y: -4, scale: 0.98 },
  animate: { opacity: 1, y: 0, scale: 1 },
  exit: { opacity: 0, y: -4, scale: 0.98 },
  transition: { duration: 0.16, ease: [0.22, 1, 0.36, 1] as const },
};

export function TitleBar() {
  const maximized = useStore((s) => s.maximized);
  const onBarMouseDown = (e: React.MouseEvent) => {
    // Fallback drag when the host does not handle app-region natively.
    if (e.button !== 0) return;
    const el = e.target as HTMLElement;
    if (el.closest('[data-nodrag]')) return;
    void request('window.drag');
  };
  return (
    <header className="titlebar" onMouseDown={onBarMouseDown} onDoubleClick={(e) => !(e.target as HTMLElement).closest('[data-nodrag]') && request('window.toggleMaximize')}>
      <div className="tb-brand">
        <Mark />
        <span>MTRTracer</span>
      </div>
      <ViewTabs />
      <CommandBar />
      <div className="tb-spacer" />
      <ExportMenu />
      {/* Linux windows keep their native title bar; custom controls are only for the frameless Windows window. */}
      {hostKind !== 'ws' && (
      <div className="tb-window" data-nodrag>
        <button className="tb-win" aria-label="Свернуть" onClick={() => request('window.minimize')}>
          <Minimize size={14} />
        </button>
        <button className="tb-win" aria-label={maximized ? 'Восстановить' : 'Развернуть'} onClick={() => request('window.toggleMaximize')}>
          {maximized ? <Restore size={13} /> : <Maximize size={12} />}
        </button>
        <button className="tb-win close" aria-label="Закрыть" onClick={() => request('window.close')}>
          <Close size={14} />
        </button>
      </div>
      )}
    </header>
  );
}

function ViewTabs() {
  const view = useStore((s) => s.view);
  const setView = useStore((s) => s.setView);
  return (
    <div className="seg view-tabs" data-nodrag role="tablist">
      <button role="tab" aria-selected={view === 'trace'} className={view === 'trace' ? 'on' : ''} onClick={() => setView('trace')}>
        Трассировка
      </button>
      <button role="tab" aria-selected={view === 'history'} className={view === 'history' ? 'on' : ''} onClick={() => setView('history')}>
        История
      </button>
    </div>
  );
}

function Mark() {
  // Three hops and a route: the product in one glyph.
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden>
      <path d="M3 13.5C6 13.5 6 4.5 9 4.5s3 9 6 9" fill="none" stroke="var(--accent)" strokeWidth="1.6" strokeLinecap="round" />
      <circle cx="3" cy="13.5" r="2" fill="var(--text)" />
      <circle cx="9" cy="4.5" r="2" fill="var(--text)" />
      <circle cx="15" cy="13.5" r="2" fill="var(--accent)" />
    </svg>
  );
}

function CommandBar() {
  const session = useStore((s) => s.session);
  const recent = useStore((s) => s.recent);
  const start = useStore((s) => s.start);
  const stop = useStore((s) => s.stop);
  const pause = useStore((s) => s.pause);
  const reset = useStore((s) => s.reset);
  const [value, setValue] = useState('');
  const [open, setOpen] = useState(false);
  const [highlight, setHighlight] = useState(-1);
  const box = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLInputElement>(null);
  useOutside(box, () => setOpen(false), open);

  useEffect(() => {
    if (session?.input && !value) setValue(session.input);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session?.sessionId]);

  useEffect(() => {
    const k = (e: KeyboardEvent) => {
      if ((e.ctrlKey && e.key.toLowerCase() === 'l') || e.key === 'F6') {
        e.preventDefault();
        input.current?.focus();
        input.current?.select();
      }
    };
    window.addEventListener('keydown', k);
    return () => window.removeEventListener('keydown', k);
  }, []);

  const state = session?.state;
  const live = state === 'running' || state === 'paused' || state === 'resolving' || state === 'waiting-network';
  const suggestions = recent.filter((r) => !value || r.toLowerCase().includes(value.toLowerCase())).slice(0, 8);

  const go = (target = value) => {
    setOpen(false);
    if (!target.trim()) {
      input.current?.focus();
      return;
    }
    setValue(target);
    void start(target);
    input.current?.blur();
  };

  return (
    <div className="cmd" data-nodrag ref={box}>
      <div className={`cmd-field ${live ? 'live' : ''}`}>
        <StateDot state={state} />
        <input
          ref={input}
          className="cmd-input"
          value={value}
          spellCheck={false}
          placeholder="IP, домен или URL: 1.1.1.1, ya.ru, https://…"
          onChange={(e) => {
            setValue(e.target.value);
            setOpen(true);
            setHighlight(-1);
          }}
          onFocus={() => setOpen(true)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') go(highlight >= 0 ? suggestions[highlight] : value);
            else if (e.key === 'ArrowDown') {
              e.preventDefault();
              setOpen(true);
              setHighlight((h) => Math.min(suggestions.length - 1, h + 1));
            } else if (e.key === 'ArrowUp') {
              e.preventDefault();
              setHighlight((h) => Math.max(-1, h - 1));
            } else if (e.key === 'Escape') {
              setOpen(false);
              input.current?.blur();
            }
          }}
        />
        <span className="kbd cmd-kbd">Ctrl L</span>
      </div>
      <AnimatePresence>
        {open && suggestions.length > 0 && (
          <motion.div className="popover cmd-pop" {...pop}>
            <div className="pop-label">Недавние</div>
            {suggestions.map((r, i) => (
              <button key={r} className={`menu-item ${i === highlight ? 'hl' : ''}`} onMouseDown={(e) => e.preventDefault()} onClick={() => go(r)}>
                <span className="mono">{r}</span>
              </button>
            ))}
          </motion.div>
        )}
      </AnimatePresence>

      {!live ? (
        <button className="btn primary cmd-go" onClick={() => go()} title="Запустить (Enter)">
          <Play size={13} /> Старт
        </button>
      ) : (
        <>
          <button className="btn cmd-go danger-hover" onClick={() => stop()} title="Остановить">
            <Stop size={12} /> Стоп
          </button>
          <button
            className={`btn icon ${state === 'paused' ? 'active' : ''}`}
            disabled={state !== 'running' && state !== 'paused'}
            onClick={() => pause(state !== 'paused')}
            title={state === 'paused' ? 'Продолжить' : 'Пауза'}
            aria-label={state === 'paused' ? 'Продолжить' : 'Пауза'}
          >
            {state === 'paused' ? <Play size={13} /> : <Pause size={13} />}
          </button>
        </>
      )}
      <button className="btn icon" disabled={!session} onClick={() => reset()} title="Сбросить статистику и начать заново" aria-label="Сбросить">
        <Restart size={15} />
      </button>
      <OptionsMenu />
      <AdapterChip />
      <VpnChip />
    </div>
  );
}

function StateDot({ state }: { state: string | undefined }) {
  const cls =
    state === 'running' ? 'run' : state === 'paused' ? 'pause' : state === 'resolving' ? 'wait' : state === 'waiting-network' || state === 'error' ? 'err' : 'idle';
  return <span className={`state-dot ${cls}`} aria-hidden />;
}

function VpnChip() {
  const vpnMode = useStore((s) => s.vpnMode);
  const setVpnMode = useStore((s) => s.setVpnMode);
  const isTunnel = useStore((s) => {
    const a = s.selectedAdapterId ? s.adapters.find((x) => x.id === s.selectedAdapterId) : s.adapters.find((x) => x.isWindowsChoice);
    return a?.kind === 'vpn';
  });
  if (!isTunnel) return null;
  return (
    <button
      className={`btn ${vpnMode ? 'active' : ''}`}
      onClick={() => setVpnMode(!vpnMode)}
      title={vpnMode ? 'VPN-режим: хопы до сервера VPN + сквозная задержка через туннель' : 'Обычный режим: через прокси-туннель хопы не видны, ответы могут быть поддельными'}
    >
      VPN-режим
    </button>
  );
}

function AdapterChip() {
  const adapters = useStore((s) => s.adapters);
  const id = useStore((s) => s.selectedAdapterId);
  const openGate = useStore((s) => s.openAdapterGate);
  const a = adapters.find((x) => x.id === id);
  const auto = adapters.find((x) => x.isWindowsChoice);
  return (
    <button className="btn adapter-chip" onClick={() => openGate(true)} title="Сменить сетевой адаптер">
      <AdapterIcon kind={a?.kind ?? auto?.kind ?? 'other'} size={14} />
      <span className="ac-name">{a ? a.name : 'Авто'}</span>
      <span className="mono ac-ip">{a ? a.ipv4[0]?.address ?? a.ipv6[0]?.address ?? '' : auto ? `→ ${auto.name}` : ''}</span>
      <Chevron size={12} />
    </button>
  );
}

function OptionsMenu() {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useOutside(ref, () => setOpen(false), open);
  const o = useStore((s) => s.options);
  const set = useStore((s) => s.setOptions);
  const live = useStore((s) => s.session?.state === 'running' || s.session?.state === 'paused');
  const reset = useStore((s) => s.reset);
  const [dirty, setDirty] = useState(false);
  const upd = (p: Partial<TraceOptions>) => {
    set(p);
    if (live) setDirty(true);
  };
  return (
    <div className="opt" ref={ref}>
      <button className={`btn icon ${open ? 'active' : ''}`} onClick={() => setOpen(!open)} title="Параметры зондирования" aria-label="Параметры">
        <Sliders size={15} />
      </button>
      <AnimatePresence>
        {open && (
          <motion.div className="popover opt-pop" {...pop}>
            <div className="opt-grid">
              <Num label="Интервал" unit="мс" value={o.intervalMs} min={100} max={60000} step={100} onChange={(v) => upd({ intervalMs: v })} hint="Пауза между раундами на каждом хопе" />
              <Num label="Таймаут" unit="мс" value={o.timeoutMs} min={200} max={10000} step={100} onChange={(v) => upd({ timeoutMs: v })} hint="Сколько ждать ответа" />
              <Num label="Размер пакета" unit="Б" value={o.packetSize} min={0} max={65000} step={8} onChange={(v) => upd({ packetSize: v })} hint="Полезная нагрузка ICMP" />
              <Num label="Макс. хопов" value={o.maxHops} min={1} max={64} step={1} onChange={(v) => upd({ maxHops: v })} />
              <Num label="Пакетов за раунд" value={o.burst} min={1} max={20} step={1} onChange={(v) => upd({ burst: v })} hint="Серия пакетов на каждый хоп" />
              <Num label="Раундов" value={o.maxRounds} min={0} max={1000000} step={10} onChange={(v) => upd({ maxRounds: v })} hint="0 = бесконечно" />
            </div>
            <div className="opt-row">
              <span>Протокол IP</span>
              <div className="seg">
                {(['auto', '4', '6'] as const).map((v) => (
                  <button key={v} className={o.ipVersion === v ? 'on' : ''} onClick={() => upd({ ipVersion: v })}>
                    {v === 'auto' ? 'Авто' : `IPv${v}`}
                  </button>
                ))}
              </div>
            </div>
            <Toggle label="Запрет фрагментации (DF)" hint="Для поиска проблем с MTU" on={o.dontFragment} onChange={(v) => upd({ dontFragment: v })} />
            <Toggle label="Имена узлов (обратный DNS)" on={o.resolveNames} onChange={(v) => upd({ resolveNames: v })} />
            <div className="opt-row">
              <Toggle label="TCP-пинг, если цель молчит" on={o.tcpFallback} onChange={(v) => upd({ tcpFallback: v })} inline />
              <input className="field sm mono opt-port" type="number" min={1} max={65535} value={o.tcpPort} onChange={(e) => upd({ tcpPort: Number(e.target.value) || 443 })} aria-label="TCP-порт" />
            </div>
            <Toggle label="Онлайн-уточнение (ip-api.com)" hint="Провайдер и AS; адреса узлов отправляются сервису" on={o.geoOnline} onChange={(v) => upd({ geoOnline: v })} />
            <label className="opt-payload">
              <span>Содержимое пакета</span>
              <input className="field sm mono" value={o.payload} maxLength={64} onChange={(e) => upd({ payload: e.target.value })} />
            </label>
            {dirty && live && (
              <div className="opt-apply">
                <span>Изменения применятся при перезапуске</span>
                <button
                  className="btn sm primary"
                  onClick={() => {
                    setDirty(false);
                    void reset();
                  }}
                >
                  Перезапустить
                </button>
              </div>
            )}
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}

function Num(props: { label: string; unit?: string; value: number; min: number; max: number; step: number; hint?: string; onChange: (v: number) => void }) {
  const [text, setText] = useState(String(props.value));
  useEffect(() => setText(String(props.value)), [props.value]);
  const commit = () => {
    const n = Math.round(Number(text));
    if (Number.isFinite(n)) props.onChange(Math.min(props.max, Math.max(props.min, n)));
    else setText(String(props.value));
  };
  return (
    <label className="num-field" title={props.hint}>
      <span>{props.label}</span>
      <div className="num-input">
        <input
          className="field sm mono num"
          value={text}
          inputMode="numeric"
          onChange={(e) => setText(e.target.value)}
          onBlur={commit}
          onKeyDown={(e) => {
            if (e.key === 'Enter') commit();
            if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
              e.preventDefault();
              const n = Math.min(props.max, Math.max(props.min, props.value + (e.key === 'ArrowUp' ? props.step : -props.step)));
              props.onChange(n);
            }
          }}
        />
        {props.unit && <span className="unit">{props.unit}</span>}
      </div>
    </label>
  );
}

function Toggle({ label, hint, on, onChange, inline }: { label: string; hint?: string; on: boolean; onChange: (v: boolean) => void; inline?: boolean }) {
  return (
    <button className={`toggle ${inline ? 'inline' : ''}`} onClick={() => onChange(!on)} role="switch" aria-checked={on}>
      <span className="toggle-text">
        <span>{label}</span>
        {hint && <span className="toggle-hint">{hint}</span>}
      </span>
      <span className={`switch ${on ? 'on' : ''}`} />
    </button>
  );
}

function ExportMenu() {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useOutside(ref, () => setOpen(false), open);
  const has = useStore((s) => s.hops.length > 0);
  const notify = useStore((s) => s.notify);
  const run = async (format: ExportFormat) => {
    setOpen(false);
    try {
      const r = await request('export.save', { format });
      if (r.copied) notify('Отчёт скопирован в буфер обмена');
      else if (r.path) notify(`Сохранено: ${r.path}`);
    } catch (e) {
      notify((e as Error).message, 'error');
    }
  };
  return (
    <div className="exp" data-nodrag ref={ref}>
      <button className="btn ghost" disabled={!has} onClick={() => setOpen(!open)} title="Экспорт отчёта">
        <Export size={15} /> Экспорт
      </button>
      <AnimatePresence>
        {open && (
          <motion.div className="popover exp-pop" {...pop}>
            <button className="menu-item" onClick={() => run('txt')}>
              Текст <span className="hint">.txt, как WinMTR</span>
            </button>
            <button className="menu-item" onClick={() => run('csv')}>
              Таблица <span className="hint">.csv</span>
            </button>
            <button className="menu-item" onClick={() => run('html')}>
              Отчёт <span className="hint">.html</span>
            </button>
            <button className="menu-item" onClick={() => run('json')}>
              Все пакеты <span className="hint">.json</span>
            </button>
            <div className="pop-sep" />
            <button className="menu-item" onClick={() => run('clipboard')}>
              Копировать как текст
            </button>
            <button
              className="menu-item"
              onClick={() => {
                setOpen(false);
                void request('shell.open', { path: 'sessions' });
              }}
            >
              <Folder size={14} /> Папка с логами сессий
            </button>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
