import { L } from '../i18n';
import { useEffect, useMemo, useState } from 'react';
import { request } from '../bridge/client';
import type { Connection } from '../bridge/protocol';
import { useStore } from '../store';
import { clock, countryName, duration } from '../lib/format';
import { Copy, Folder, Play, Search } from './Icons';
import { useContextMenu } from './ContextMenu';

const icons = new Map<string, string | null | Promise<void>>();

function ProcIcon({ path }: { path: string | null }) {
  const [, force] = useState(0);
  if (!path) return <span className="proc-icon blank" />;
  const v = icons.get(path);
  if (v === undefined) {
    icons.set(
      path,
      request('process.icon', { path })
        .then((r) => void icons.set(path, r.dataUrl))
        .catch(() => void icons.set(path, null))
        .finally(() => force((x) => x + 1)),
    );
  }
  return typeof v === 'string' ? <img className="proc-icon" src={v} alt="" /> : <span className="proc-icon blank" />;
}

type SortKey = 'process' | 'remote' | 'state' | 'age';

export function ConnectionsPanel() {
  const items = useStore((s) => s.connections);
  const log = useStore((s) => s.connLog);
  const start = useStore((s) => s.start);
  const [query, setQuery] = useState('');
  const [externalOnly, setExternalOnly] = useState(true);
  const [view, setView] = useState<'table' | 'log'>('table');
  const [sort, setSort] = useState<SortKey>('process');
  const [now, setNow] = useState(Date.now());
  const openMenu = useContextMenu((s) => s.open);
  const tunnels = useStore((s) => s.tunnels);
  const notify = useStore((s) => s.notify);
  const setVpnMode = useStore((s) => s.setVpnMode);

  const menu = (e: React.MouseEvent, c: Connection) => {
    const remote = c.remoteAddr;
    const ep = remote ? (c.family === 6 ? `[${remote}]:${c.remotePort}` : `${remote}:${c.remotePort}`) : null;
    const traceable = !!remote && c.remoteKind === 'public';
    const copy = (text: string) => {
      void request('clipboard.copy', { text });
      notify(`${L('Скопировано', 'Copied')}: ${text}`);
    };
    openMenu(e, [
      { title: `${c.process} (PID ${c.pid}) → ${c.remoteHost ?? ep ?? L('нет удалённого адреса', 'no remote address')}` },
      { label: L('Трассировать до узла', 'Trace to this host'), icon: <Play size={11} />, hint: remote ?? undefined, disabled: !traceable, onSelect: () => { void setVpnMode(false).then(() => start(remote!)); } },
      ...(tunnels.length > 0
        ? [{ label: L('Трассировать через VPN', 'Trace through the VPN'), icon: <Play size={11} />, hint: L('VPN-режим', 'VPN mode'), disabled: !traceable, onSelect: () => { void setVpnMode(true).then(() => start(c.remoteHost ?? remote!)); } }]
        : []),
      { separator: true as const },
      { label: L('Копировать IP', 'Copy IP'), icon: <Copy size={13} />, disabled: !remote, onSelect: () => copy(remote!) },
      { label: L('Копировать IP:порт', 'Copy IP:port'), icon: <Copy size={13} />, disabled: !ep, onSelect: () => copy(ep!) },
      { label: L('Копировать имя хоста', 'Copy host name'), icon: <Copy size={13} />, disabled: !c.remoteHost, onSelect: () => copy(c.remoteHost!) },
      { separator: true as const },
      { label: `${L('Показать только', 'Show only')} ${c.process}`, icon: <Search size={13} />, onSelect: () => setQuery(c.process) },
      { label: L('Открыть папку процесса', 'Open process folder'), icon: <Folder size={13} />, disabled: !c.path, onSelect: () => void request('shell.reveal', { path: c.path! }).catch((err: Error) => notify(err.message, 'error')) },
    ]);
  };

  useEffect(() => {
    void request('connections.subscribe', { on: true });
    const t = window.setInterval(() => setNow(Date.now()), 1000);
    return () => {
      void request('connections.subscribe', { on: false });
      window.clearInterval(t);
    };
  }, []);

  const rows = useMemo(() => {
    const q = query.trim().toLowerCase();
    const list = items.filter(
      (c) =>
        (!externalOnly || (c.remoteAddr && c.remoteKind === 'public')) &&
        (!q ||
          c.process.toLowerCase().includes(q) ||
          String(c.pid) === q ||
          (c.remoteAddr ?? '').includes(q) ||
          (c.remoteHost ?? '').toLowerCase().includes(q) ||
          String(c.remotePort ?? '') === q ||
          String(c.localPort) === q),
    );
    const cmp: Record<SortKey, (a: Connection, b: Connection) => number> = {
      process: (a, b) => a.process.localeCompare(b.process) || a.pid - b.pid,
      remote: (a, b) => (a.remoteAddr ?? '').localeCompare(b.remoteAddr ?? ''),
      state: (a, b) => a.state.localeCompare(b.state),
      age: (a, b) => a.firstSeen - b.firstSeen,
    };
    return list.sort(cmp[sort]);
  }, [items, query, externalOnly, sort]);

  const procCount = new Set(rows.map((r) => r.pid)).size;

  return (
    <div className="panel conns">
      <div className="panel-tools">
        <div className="seg">
          <button className={view === 'table' ? 'on' : ''} onClick={() => setView('table')}>
            {L('Сейчас', 'Now')}
          </button>
          <button className={view === 'log' ? 'on' : ''} onClick={() => setView('log')}>
            {L('Журнал', 'Log')} {log.length > 0 && <span className="muted num">{log.length}</span>}
          </button>
        </div>
        <label className="search">
          <Search size={13} />
          <input className="field sm" placeholder={L('Процесс, PID, адрес, порт', 'Process, PID, address, port')} value={query} onChange={(e) => setQuery(e.target.value)} />
        </label>
        <button className={`btn sm ${externalOnly ? 'active' : ''}`} onClick={() => setExternalOnly(!externalOnly)} title={L('Скрыть LISTEN, UDP без адресата и локальную сеть', 'Hide LISTEN, unconnected UDP and the local network')}>
          {L('Только интернет', 'Internet only')}
        </button>
        <span className="panel-count num">
          {rows.length} {L('соединений', 'connections')} · {procCount} {L('процессов', 'processes')}
        </span>
      </div>
      {view === 'table' ? (
        <div className="conn-scroll">
          <table className="conn-table">
            <thead>
              <tr>
                <th onClick={() => setSort('process')} className={sort === 'process' ? 'on' : ''}>
                  {L('Процесс', 'Process')}
                </th>
                <th>{L('Прот.', 'Proto')}</th>
                <th>{L('Локальный', 'Local')}</th>
                <th onClick={() => setSort('remote')} className={sort === 'remote' ? 'on' : ''}>
                  {L('Удалённый', 'Remote')}
                </th>
                <th>{L('Где', 'Where')}</th>
                <th onClick={() => setSort('state')} className={sort === 'state' ? 'on' : ''}>
                  {L('Состояние', 'State')}
                </th>
                <th>{L('Интерфейс', 'Interface')}</th>
                <th onClick={() => setSort('age')} className={`r ${sort === 'age' ? 'on' : ''}`}>
                  {L('Открыто', 'Opened')}
                </th>
                <th />
              </tr>
            </thead>
            <tbody>
              {rows.length === 0 && (
                <tr>
                  <td colSpan={9} className="empty">
                    {items.length === 0 ? L('Читаем таблицы TCP/UDP системы…', 'Reading the system TCP/UDP tables…') : L('Нет соединений под фильтр.', 'No connections match the filter.')}
                  </td>
                </tr>
              )}
              {rows.map((c) => (
                <tr key={c.key} onContextMenu={(e) => menu(e, c)}>
                  <td className="proc" title={c.path ?? undefined}>
                    <ProcIcon path={c.path} />
                    <span>{c.process}</span>
                    <span className="muted num">{c.pid}</span>
                  </td>
                  <td className="muted">{c.proto.toUpperCase()}</td>
                  <td className="mono muted selectable">
                    {c.family === 6 ? `[${c.localAddr}]` : c.localAddr}:{c.localPort}
                  </td>
                  <td className="mono selectable" title={c.remoteHost ?? undefined}>
                    {c.remoteAddr ? (
                      <>
                        {c.remoteHost ?? (c.family === 6 ? `[${c.remoteAddr}]` : c.remoteAddr)}
                        <span className="muted">:{c.remotePort}</span>
                      </>
                    ) : (
                      <span className="muted">—</span>
                    )}
                  </td>
                  <td className="where">
                    {c.remoteCountry && <span title={countryName(c.remoteCountry) ?? undefined}>{c.remoteCountry}</span>}
                    {c.remoteCity && <span className="muted"> {c.remoteCity}</span>}
                    {c.remoteAsName && <span className="muted"> · {c.remoteAsName}</span>}
                  </td>
                  <td>
                    <span className={`st-${c.state.toLowerCase()}`}>{c.state}</span>
                  </td>
                  <td className="muted">{c.adapterName ?? '—'}</td>
                  <td className="r num muted">{duration(now - c.firstSeen)}</td>
                  <td className="r">
                    {c.remoteAddr && c.remoteKind === 'public' && (
                      <button className="btn sm ghost icon" onClick={() => start(c.remoteAddr!)} title={`${L('Трассировать до', 'Trace to')} ${c.remoteAddr}`}>
                        <Play size={11} />
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="conn-log">
          {log.length === 0 && <div className="empty">{L('Здесь появятся открытия и закрытия соединений, пока открыта эта вкладка.', 'Connections opening and closing appear here while this tab is open.')}</div>}
          {[...log].reverse().map((e, i) => (
            <div key={i} className={`cl-row ${e.kind}`}>
              <span className="mono muted">{clock(e.t, false)}</span>
              <span className="cl-kind">{e.kind === 'open' ? L('+ открыто', '+ opened') : L('− закрыто', '− closed')}</span>
              <span>{e.c.process}</span>
              <span className="muted num">{e.c.pid}</span>
              <span className="mono">
                {e.c.remoteHost ?? e.c.remoteAddr}:{e.c.remotePort}
              </span>
              <span className="muted">{e.c.proto.toUpperCase()}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
