import { L } from '../i18n';
import { useEffect, useMemo, useState } from 'react';
import { AnimatePresence, motion } from 'motion/react';
import { request } from '../bridge/client';
import type { AdapterInfo } from '../bridge/protocol';
import { useStore } from '../store';
import { bits, linkSpeed } from '../lib/format';
import { AdapterIcon, Alert, ChevronRight, Close, Restart, Route } from './Icons';
import './adapters.css';

const AUTO = '__auto__';

const kindLabel = (k: string) =>
  ({
    ethernet: 'Ethernet',
    wifi: 'Wi-Fi',
    vpn: L('VPN / туннель', 'VPN / tunnel'),
    virtual: L('Виртуальный', 'Virtual'),
    loopback: 'Loopback',
    cellular: L('Мобильная сеть', 'Cellular'),
    other: L('Другой', 'Other'),
  })[k] ?? k;

function usable(a: AdapterInfo) {
  return a.status === 'up' && a.kind !== 'loopback' && (a.ipv4.length > 0 || a.ipv6.some((x) => !x.address.startsWith('fe80')));
}

export function AdapterGate() {
  const open = useStore((s) => s.adapterGateOpen);
  const ready = useStore((s) => s.ready);
  return <AnimatePresence>{open && ready && <GatePanel key="gate" />}</AnimatePresence>;
}

function GatePanel() {
  const adapters = useStore((s) => s.adapters);
  const loading = useStore((s) => s.adaptersLoading);
  const selectedId = useStore((s) => s.selectedAdapterId);
  const choose = useStore((s) => s.chooseAdapter);
  const openGate = useStore((s) => s.openAdapterGate);
  const reload = useStore((s) => s.loadAdapters);
  const hasSession = useStore((s) => !!s.session);
  const [pick, setPick] = useState<string>(selectedId ?? AUTO);
  const [showAll, setShowAll] = useState(false);
  const storeVpn = useStore((s) => s.vpnMode);
  const tunnels = useStore((s) => s.tunnels);
  const [vpnMode, setVpnMode] = useState<boolean>(storeVpn || true);

  useEffect(() => {
    void request('adapters.watch', { on: true });
  }, []);

  const { main, rest } = useMemo(() => {
    const main = adapters.filter(usable);
    const rest = adapters.filter((a) => !usable(a));
    return { main, rest };
  }, [adapters]);

  useEffect(() => {
    if (pick !== AUTO && adapters.length && !adapters.some((a) => a.id === pick)) setPick(AUTO);
  }, [adapters, pick]);

  const windowsChoice = adapters.find((a) => a.isWindowsChoice);
  const order = [AUTO, ...main.map((a) => a.id), ...(showAll ? rest.map((a) => a.id) : [])];
  const picked = adapters.find((a) => a.id === pick);
  const canConfirm = pick === AUTO || (picked && usable(picked));

  const pickedKind = pick === AUTO ? adapters.find((a) => a.isWindowsChoice)?.kind : picked?.kind;
  const isTunnel = pickedKind === 'vpn';
  const tunnel = tunnels.find((t) => t.adapterId === (pick === AUTO ? adapters.find((a) => a.isWindowsChoice)?.id : pick));
  const confirm = () => {
    if (canConfirm) void choose(pick === AUTO ? null : pick, isTunnel && vpnMode);
  };

  useEffect(() => {
    const k = (e: KeyboardEvent) => {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        const i = order.indexOf(pick);
        const n = order[Math.max(0, Math.min(order.length - 1, i + (e.key === 'ArrowDown' ? 1 : -1)))];
        if (n) setPick(n);
      } else if (e.key === 'Enter') {
        e.preventDefault();
        confirm();
      } else if (e.key === 'Escape' && hasSession) {
        openGate(false);
      }
    };
    window.addEventListener('keydown', k);
    return () => window.removeEventListener('keydown', k);
  });

  return (
    <motion.div className="gate" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} transition={{ duration: 0.18 }}>
      <motion.section
        className="gate-panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby="gate-title"
        initial={{ opacity: 0, y: 10, scale: 0.985 }}
        animate={{ opacity: 1, y: 0, scale: 1 }}
        exit={{ opacity: 0, y: 6, scale: 0.99 }}
        transition={{ duration: 0.26, ease: [0.16, 1, 0.3, 1] }}
      >
        <header className="gate-head">
          <div>
            <h1 id="gate-title">{L('Через какой адаптер трассировать?', 'Which adapter should the trace use?')}</h1>
            <p>{L('Пакеты уйдут с адреса выбранного интерфейса. Так можно проверить маршрут мимо VPN или, наоборот, через туннель.', 'Packets leave from the address of the chosen interface. This way you can test the route around the VPN or, the other way round, through the tunnel.')}</p>
          </div>
          <div className="gate-head-actions">
            <button className="btn ghost icon" onClick={() => reload()} title={L('Обновить список', 'Refresh list')} aria-label={L('Обновить список', 'Refresh list')}>
              <Restart size={15} className={loading ? 'spin' : ''} />
            </button>
            {hasSession && (
              <button className="btn ghost icon" onClick={() => openGate(false)} title={L('Закрыть (Esc)', 'Close (Esc)')} aria-label={L('Закрыть', 'Close')}>
                <Close size={15} />
              </button>
            )}
          </div>
        </header>

        <div className="gate-list" role="listbox" aria-label={L('Сетевые адаптеры', 'Network adapters')}>
          <AutoRow selected={pick === AUTO} onPick={() => setPick(AUTO)} onConfirm={confirm} via={windowsChoice} />
          {loading && adapters.length === 0 && [0, 1, 2].map((i) => <div key={i} className="ad-skeleton" />)}
          {main.map((a) => (
            <AdapterRow key={a.id} a={a} selected={pick === a.id} onPick={() => setPick(a.id)} onConfirm={confirm} />
          ))}
          {rest.length > 0 && (
            <button className="gate-more" onClick={() => setShowAll(!showAll)}>
              <ChevronRight size={13} className={showAll ? 'rot' : ''} />
              {showAll ? L('Скрыть отключённые и служебные', 'Hide disconnected and system') : L('Показать отключённые и служебные', 'Show disconnected and system')} ({rest.length})
            </button>
          )}
          {showAll && rest.map((a) => <AdapterRow key={a.id} a={a} selected={pick === a.id} onPick={() => setPick(a.id)} onConfirm={confirm} disabled />)}
        </div>

        {isTunnel && (
          <div className="gate-vpn">
            <button className="toggle" role="switch" aria-checked={vpnMode} onClick={() => setVpnMode(!vpnMode)}>
              <span className="toggle-text">
                <span>{L('VPN-режим (рекомендуется для туннелей)', 'VPN mode (recommended for tunnels)')}</span>
                <span className="toggle-hint">
                  {L('Прокси-туннель не пропускает пакеты с TTL, пошаговая трассировка через него невозможна. В VPN-режиме видны настоящие хопы до сервера VPN', 'A proxy tunnel does not pass packets with a TTL, so hop-by-hop tracing through it is impossible. VPN mode shows the real hops to the VPN server')}
                  {tunnel?.servers[0] ? ` (${tunnel.servers[0].ip}, ${tunnel.process ?? L('клиент', 'client')})` : ''} {L('через', 'via')} {tunnel?.carrierName ?? L('физический адаптер', 'the physical adapter')}, {L('а дальше честная сквозная задержка до цели через туннель.', 'and then an honest end-to-end latency to the target through the tunnel.')}
                </span>
              </span>
              <span className={`switch ${vpnMode ? 'on' : ''}`} />
            </button>
            {!vpnMode && <div className="gate-vpn-warn">{L('Без VPN-режима туннель, скорее всего, будет отвечать на пинг сам, и цифры будут ненастоящими.', 'Without VPN mode the tunnel will most likely answer pings itself and the numbers will not be real.')}</div>}
          </div>
        )}
        <footer className="gate-foot">
          <span className="gate-foot-hint">
            <span className="kbd">↑</span> <span className="kbd">↓</span> {L('выбор', 'choose')}, <span className="kbd">Enter</span> {L('подтвердить', 'confirm')}
          </span>
          <button className="btn primary gate-ok" disabled={!canConfirm} onClick={confirm}>
            {pick === AUTO ? L('Использовать авто', 'Use auto') : `${L('Использовать', 'Use')} ${picked?.name ?? ''}`}
          </button>
        </footer>
      </motion.section>
    </motion.div>
  );
}

function AutoRow({ selected, onPick, onConfirm, via }: { selected: boolean; onPick: () => void; onConfirm: () => void; via?: AdapterInfo }) {
  return (
    <div className={`ad-row auto ${selected ? 'sel' : ''}`} role="option" aria-selected={selected} onClick={onPick} onDoubleClick={onConfirm}>
      <span className="ad-radio" />
      <span className="ad-icon">
        <Route size={16} />
      </span>
      <div className="ad-main">
        <div className="ad-title">
          <span className="ad-name">{L('Авто', 'Auto')}</span>
          <span className="ad-desc">{L('как решит таблица маршрутизации системы', 'as the system routing table decides')}</span>
        </div>
        <div className="ad-sub">
          {via ? (
            <>
              {L('сейчас трафик идёт через', 'traffic currently goes via')} <b>{via.name}</b>
              {via.kind === 'vpn' && <span className="badge warn">VPN</span>}
            </>
          ) : (
            L('маршрут по умолчанию не найден', 'no default route found')
          )}
        </div>
      </div>
    </div>
  );
}

function AdapterRow({ a, selected, onPick, onConfirm, disabled }: { a: AdapterInfo; selected: boolean; onPick: () => void; onConfirm: () => void; disabled?: boolean }) {
  const traffic = useStore((s) => s.traffic[a.id]);
  const tunnel = useStore((s) => s.tunnels.find((t) => t.adapterId === a.id));
  const v4 = a.ipv4.map((x) => x.address);
  const v6 = a.ipv6.map((x) => x.address).filter((x) => !x.startsWith('fe80'));
  const speed = linkSpeed(a.speedBps);
  return (
    <div
      className={`ad-row ${selected ? 'sel' : ''} ${disabled ? 'off' : ''}`}
      role="option"
      aria-selected={selected}
      aria-disabled={disabled}
      onClick={onPick}
      onDoubleClick={() => !disabled && onConfirm()}
    >
      <span className="ad-radio" />
      <span className={`ad-icon k-${a.kind}`}>
        <AdapterIcon kind={a.kind} size={16} />
      </span>
      <div className="ad-main">
        <div className="ad-title">
          <span className="ad-name">{a.name}</span>
          <span className="ad-desc">{a.description}</span>
        </div>
        <div className="ad-sub">
          <span>{kindLabel(a.kind)}</span>
          {a.isWindowsChoice && <span className="badge accent">{L('Маршрут по умолчанию', 'Default route')}</span>}
          {a.status !== 'up' && <span className="badge">{a.status === 'down' ? L('Отключён', 'Disconnected') : a.status}</span>}
          {a.status === 'up' && !a.hasRoute && a.kind !== 'loopback' && <span className="badge warn">{L('Нет выхода в интернет', 'No internet route')}</span>}
          {tunnel?.process && <span className="badge">{tunnel.process}</span>}
          {tunnel?.carrierName && <span className="ad-note">{L('трафик идёт через', 'traffic goes via')} {tunnel.carrierName}</span>}
        </div>
      </div>
      <div className="ad-addr">
        <span className="mono">{v4[0] ?? v6[0] ?? '—'}</span>
        {a.status === 'up' && traffic && (
          <span className="ad-rate num">
            ↓ {bits(traffic.downBps)} <span className="muted">·</span> ↑ {bits(traffic.upBps)}
          </span>
        )}
      </div>
      <AnimatePresence initial={false}>
        {selected && (
          <motion.dl
            className="ad-details"
            initial={{ opacity: 0, height: 0 }}
            animate={{ opacity: 1, height: 'auto' }}
            exit={{ opacity: 0, height: 0 }}
            transition={{ duration: 0.2, ease: [0.22, 1, 0.36, 1] }}
          >
            <Detail k="IPv4" v={v4.length ? a.ipv4.map((x) => `${x.address}/${x.prefix}`).join(', ') : null} mono />
            <Detail k="IPv6" v={v6.length ? v6.join(', ') : null} mono />
            <Detail k={L('Шлюз', 'Gateway')} v={a.gateways.join(', ') || (a.isWindowsChoice ? L('нет (точка-точка)', 'none (point-to-point)') : null)} mono />
            <Detail k="DNS" v={a.dns.join(', ') || null} mono />
            <Detail k="MAC" v={a.mac} mono />
            <Detail k={L('Скорость', 'Speed')} v={speed} />
            <Detail k="MTU" v={a.mtu?.toString() ?? null} mono />
            <Detail k={L('Адрес', 'Address')} v={a.dhcp ? 'DHCP' : L('статический', 'static')} />
            {tunnel && (
              <div className="ad-warn info">
                {tunnel.explanation}
                {tunnel.servers.length > 0 && ` ${L('Сервер', 'Server')}: ${tunnel.servers.map((x) => `${x.ip}:${x.port}`).slice(0, 3).join(', ')}.`}
              </div>
            )}
            {!a.hasRoute && a.status === 'up' && a.kind !== 'loopback' && (
              <div className="ad-warn">
                <Alert size={14} /> {L('У адаптера нет маршрута в интернет: трассировка дальше локальной сети, скорее всего, не пройдёт.', 'The adapter has no route to the internet: the trace will most likely not get past the local network.')}
              </div>
            )}
          </motion.dl>
        )}
      </AnimatePresence>
    </div>
  );
}

function Detail({ k, v, mono }: { k: string; v: string | null; mono?: boolean }) {
  return (
    <div className="ad-d">
      <dt>{k}</dt>
      <dd className={mono ? 'mono selectable' : ''}>{v ?? <span className="muted">—</span>}</dd>
    </div>
  );
}
