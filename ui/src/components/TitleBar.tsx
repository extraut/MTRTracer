import { useEffect, useRef, useState } from 'react';
import { AnimatePresence, motion } from 'motion/react';
import { hostKind, request } from '../bridge/client';
import type { ExportFormat, TraceOptions } from '../bridge/protocol';
import { useStore } from '../store';
import { AdapterIcon, Chevron, Close, Export, Folder, Gear, Maximize, Minimize, Pause, Play, Restart, Restore, Sliders, Stop } from './Icons';
import './titlebar.css';
import { L } from '../i18n';

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
      <SettingsButton />
      {/* Linux windows keep their native title bar; custom controls are only for the frameless Windows window. */}
      {hostKind !== 'ws' && (
      <div className="tb-window" data-nodrag>
        <button className="tb-win" aria-label={L('Свернуть', 'Minimize')} onClick={() => request('window.minimize')}>
          <Minimize size={14} />
        </button>
        <button className="tb-win" aria-label={maximized ? L('Восстановить', 'Restore') : L('Развернуть', 'Maximize')} onClick={() => request('window.toggleMaximize')}>
          {maximized ? <Restore size={13} /> : <Maximize size={12} />}
        </button>
        <button className="tb-win close" aria-label={L('Закрыть', 'Close')} onClick={() => request('window.close')}>
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
        {L('Трассировка', 'Trace')}
      </button>
      <button role="tab" aria-selected={view === 'history'} className={view === 'history' ? 'on' : ''} onClick={() => setView('history')}>
        {L('История', 'History')}
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
          placeholder={L('IP, домен или URL: 1.1.1.1, ya.ru, https://…', 'IP, domain or URL: 1.1.1.1, example.com, https://…')}
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
            <div className="pop-label">{L('Недавние', 'Recent')}</div>
            {suggestions.map((r, i) => (
              <button key={r} className={`menu-item ${i === highlight ? 'hl' : ''}`} onMouseDown={(e) => e.preventDefault()} onClick={() => go(r)}>
                <span className="mono">{r}</span>
              </button>
            ))}
          </motion.div>
        )}
      </AnimatePresence>

      {!live ? (
        <button className="btn primary cmd-go" onClick={() => go()} title={L('Запустить (Enter)', 'Start (Enter)')}>
          <Play size={13} /> {L('Старт', 'Start')}
        </button>
      ) : (
        <>
          <button className="btn cmd-go danger-hover" onClick={() => stop()} title={L('Остановить', 'Stop')}>
            <Stop size={12} /> {L('Стоп', 'Stop')}
          </button>
          <button
            className={`btn icon ${state === 'paused' ? 'active' : ''}`}
            disabled={state !== 'running' && state !== 'paused'}
            onClick={() => pause(state !== 'paused')}
            title={state === 'paused' ? L('Продолжить', 'Resume') : L('Пауза', 'Pause')}
            aria-label={state === 'paused' ? L('Продолжить', 'Resume') : L('Пауза', 'Pause')}
          >
            {state === 'paused' ? <Play size={13} /> : <Pause size={13} />}
          </button>
        </>
      )}
      <button className="btn icon" disabled={!session} onClick={() => reset()} title={L('Сбросить статистику и начать заново', 'Reset statistics and start over')} aria-label={L('Сбросить', 'Reset')}>
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
      title={vpnMode ? L('VPN-режим: хопы до сервера VPN + сквозная задержка через туннель', 'VPN mode: hops to the VPN server + end-to-end latency through the tunnel') : L('Обычный режим: через прокси-туннель хопы не видны, ответы могут быть поддельными', 'Normal mode: hops are invisible through a proxy tunnel and replies may be faked')}
    >
      {L('VPN-режим', 'VPN mode')}
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
    <button className="btn adapter-chip" onClick={() => openGate(true)} title={L('Сменить сетевой адаптер', 'Change network adapter')}>
      <AdapterIcon kind={a?.kind ?? auto?.kind ?? 'other'} size={14} />
      <span className="ac-name">{a ? a.name : L('Авто', 'Auto')}</span>
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
      <button className={`btn icon ${open ? 'active' : ''}`} onClick={() => setOpen(!open)} title={L('Параметры зондирования', 'Probe settings')} aria-label={L('Параметры зондирования', 'Probe settings')}>
        <Sliders size={15} />
      </button>
      <AnimatePresence>
        {open && (
          <motion.div className="popover opt-pop" {...pop}>
            <div className="opt-grid">
              <Num label={L('Интервал', 'Interval')} unit={L('мс', 'ms')} value={o.intervalMs} min={100} max={60000} step={100} onChange={(v) => upd({ intervalMs: v })} hint={L('Пауза между раундами на каждом хопе', 'Pause between rounds on every hop')} />
              <Num label={L('Таймаут', 'Timeout')} unit={L('мс', 'ms')} value={o.timeoutMs} min={200} max={10000} step={100} onChange={(v) => upd({ timeoutMs: v })} hint={L('Сколько ждать ответа', 'How long to wait for a reply')} />
              <Num label={L('Размер пакета', 'Packet size')} unit={L('Б', 'B')} value={o.packetSize} min={0} max={65000} step={8} onChange={(v) => upd({ packetSize: v })} hint={L('Полезная нагрузка ICMP', 'ICMP payload')} />
              <Num label={L('Макс. хопов', 'Max hops')} value={o.maxHops} min={1} max={64} step={1} onChange={(v) => upd({ maxHops: v })} />
              <Num label={L('Пакетов за раунд', 'Packets per round')} value={o.burst} min={1} max={20} step={1} onChange={(v) => upd({ burst: v })} hint={L('Серия пакетов на каждый хоп', 'Burst of packets per hop')} />
              <Num label={L('Раундов', 'Rounds')} value={o.maxRounds} min={0} max={1000000} step={10} onChange={(v) => upd({ maxRounds: v })} hint={L('0 = бесконечно', '0 = endless')} />
            </div>
            <div className="opt-row">
              <span>{L('Протокол IP', 'IP version')}</span>
              <div className="seg">
                {(['auto', '4', '6'] as const).map((v) => (
                  <button key={v} className={o.ipVersion === v ? 'on' : ''} onClick={() => upd({ ipVersion: v })}>
                    {v === 'auto' ? L('Авто', 'Auto') : `IPv${v}`}
                  </button>
                ))}
              </div>
            </div>
            <Toggle label={L('Запрет фрагментации (DF)', "Don't fragment (DF)")} hint={L('Для поиска проблем с MTU', 'To find MTU problems')} on={o.dontFragment} onChange={(v) => upd({ dontFragment: v })} />
            <Toggle label={L('Имена узлов (обратный DNS)', 'Host names (reverse DNS)')} on={o.resolveNames} onChange={(v) => upd({ resolveNames: v })} />
            <div className="opt-row">
              <Toggle label={L('TCP-пинг, если цель молчит', 'TCP ping if the target is silent')} on={o.tcpFallback} onChange={(v) => upd({ tcpFallback: v })} inline />
              <input className="field sm mono opt-port" type="number" min={1} max={65535} value={o.tcpPort} onChange={(e) => upd({ tcpPort: Number(e.target.value) || 443 })} aria-label={L('TCP-порт', 'TCP port')} />
            </div>
            <Toggle label={L('Онлайн-уточнение геоданных', 'Online geo refinement')} hint={L('Адреса узлов отправляются онлайн-источникам; какие именно — в «Данные и обновления»', 'Router addresses are sent to online sources; which ones — in "Data & updates"')} on={o.geoOnline} onChange={(v) => upd({ geoOnline: v })} />
            <label className="opt-payload">
              <span>{L('Содержимое пакета', 'Packet payload')}</span>
              <input className="field sm mono" value={o.payload} maxLength={64} onChange={(e) => upd({ payload: e.target.value })} />
            </label>
            {dirty && live && (
              <div className="opt-apply">
                <span>{L('Изменения применятся при перезапуске', 'Changes apply on restart')}</span>
                <button
                  className="btn sm primary"
                  onClick={() => {
                    setDirty(false);
                    void reset();
                  }}
                >
                  {L('Перезапустить', 'Restart')}
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

function SettingsButton() {
  const open = useStore((s) => s.openSettings);
  const update = useStore((s) => s.updateAvailable);
  return (
    <button className={`btn icon tb-settings ${update ? 'has-update' : ''}`} data-nodrag onClick={() => open('general')} title={L('Настройки, данные и обновления', 'Settings, data and updates')} aria-label={L('Настройки', 'Settings')}>
      <Gear size={15} />
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
      if (r.copied) notify(L('Отчёт скопирован в буфер обмена', 'Report copied to the clipboard'));
      else if (r.path) notify(L(`Сохранено: ${r.path}`, `Saved: ${r.path}`));
    } catch (e) {
      notify((e as Error).message, 'error');
    }
  };
  return (
    <div className="exp" data-nodrag ref={ref}>
      <button className="btn ghost" disabled={!has} onClick={() => setOpen(!open)} title={L('Экспорт отчёта', 'Export report')}>
        <Export size={15} /> {L('Экспорт', 'Export')}
      </button>
      <AnimatePresence>
        {open && (
          <motion.div className="popover exp-pop" {...pop}>
            <button className="menu-item" onClick={() => run('txt')}>
              {L('Текст', 'Text')} <span className="hint">{L('.txt, как WinMTR', '.txt, like WinMTR')}</span>
            </button>
            <button className="menu-item" onClick={() => run('csv')}>
              {L('Таблица', 'Table')} <span className="hint">.csv</span>
            </button>
            <button className="menu-item" onClick={() => run('html')}>
              {L('Отчёт', 'Report')} <span className="hint">.html</span>
            </button>
            <button className="menu-item" onClick={() => run('json')}>
              {L('Все пакеты', 'All packets')} <span className="hint">.json</span>
            </button>
            <div className="pop-sep" />
            <button className="menu-item" onClick={() => run('clipboard')}>
              {L('Копировать как текст', 'Copy as text')}
            </button>
            <button
              className="menu-item"
              onClick={() => {
                setOpen(false);
                void request('shell.open', { path: 'sessions' });
              }}
            >
              <Folder size={14} /> {L('Папка с логами сессий', 'Session logs folder')}
            </button>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
