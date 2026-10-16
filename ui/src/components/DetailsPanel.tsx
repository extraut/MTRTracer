import { L, locale } from '../i18n';
import { useEffect, useState } from 'react';
import { request } from '../bridge/client';
import type { ResolveAttempt } from '../bridge/protocol';
import { useStore } from '../store';
import { countryName, duration, ms } from '../lib/format';
import { Folder } from './Icons';

const viaText = (v: string) =>
  ({
    literal: L('IP-адрес, DNS не нужен', 'IP address, no DNS needed'),
    system: L('системный резолвер', 'system resolver'),
    'adapter-dns': L('DNS-сервер адаптера (фолбэк)', 'adapter DNS server (fallback)'),
    'public-dns': L('публичный DNS (фолбэк)', 'public DNS (fallback)'),
    none: L('не удалось', 'failed'),
  })[v] ?? v;

export function DetailsPanel() {
  const session = useStore((s) => s.session);
  const resolved = useStore((s) => s.resolved);
  const adapters = useStore((s) => s.adapters);
  const adapterId = useStore((s) => s.selectedAdapterId);
  const origin = useStore((s) => s.origin);
  const vpn = useStore((s) => s.vpn);
  const geo = useStore((s) => s.geoStatus);
  const notes = useStore((s) => s.notes);
  const round = useStore((s) => s.round);
  const elapsed = useStore((s) => s.elapsedMs);
  const hops = useStore((s) => s.hops);
  const options = useStore((s) => s.options);
  const setOptions = useStore((s) => s.setOptions);
  const notify = useStore((s) => s.notify);
  const providers = useStore((s) => s.providers);
  const openSettings = useStore((s) => s.openSettings);
  const [pmtu, setPmtu] = useState<{ busy: boolean; mtu?: number | null; ms?: number }>({ busy: false });
  const [bench, setBench] = useState<{ busy: boolean; host?: string; results?: ResolveAttempt[] }>({ busy: false });

  const adapter = adapters.find((a) => a.id === (session?.adapterId ?? adapterId));
  const sent = hops.reduce((a, h) => a + h.sent, 0);

  const runPmtu = async () => {
    setPmtu({ busy: true });
    try {
      const r = await request('trace.pmtu');
      setPmtu({ busy: false, mtu: r.mtu, ms: r.ms });
    } catch (e) {
      setPmtu({ busy: false });
      notify((e as Error).message, 'error');
    }
  };
  const runBench = async () => {
    setBench({ busy: true });
    try {
      const r = await request('trace.dnsBench', { host: session?.host ?? undefined });
      setBench({ busy: false, host: r.host, results: r.results });
    } catch (e) {
      setBench({ busy: false });
      notify((e as Error).message, 'error');
    }
  };

  return (
    <div className="panel details">
      <section>
        <h3>{L('Цель', 'Target')}</h3>
        <dl>
          <Row k={L('Ввод', 'Input')} v={session?.input} mono />
          <Row k={L('Хост', 'Host')} v={resolved?.host ?? session?.host} mono />
          <Row k="IP" v={resolved?.addresses.length ? resolved.addresses.join(', ') : session?.targetIp} mono />
          <Row k={L('Резолв', 'Resolved')} v={resolved ? `${viaText(resolved.via)}${resolved.ms ? `, ${ms(resolved.ms)} ${L('мс', 'ms')}` : ''}` : null} />
          {session?.port && <Row k={L('Порт', 'Port')} v={String(session.port)} mono />}
        </dl>
        {resolved && resolved.attempts.length > 1 && (
          <table className="mini">
            <tbody>
              {resolved.attempts.map((a, i) => (
                <tr key={i}>
                  <td className="mono">{a.resolver}</td>
                  <td className={a.ok ? 'ok' : 'bad'}>{a.ok ? L('ответ', 'answer') : a.error}</td>
                  <td className="r num">{ms(a.ms)} {L('мс', 'ms')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        <div className="sec-actions">
          <button className="btn sm" onClick={runBench} disabled={bench.busy}>
            {bench.busy ? L('Опрашиваем DNS…', 'Querying DNS…') : L('Сравнить DNS-серверы', 'Compare DNS servers')}
          </button>
        </div>
        {bench.results && (
          <table className="mini">
            <tbody>
              {bench.results
                .slice()
                .sort((a, b) => (a.ok === b.ok ? (a.ms ?? 9e9) - (b.ms ?? 9e9) : a.ok ? -1 : 1))
                .map((a) => (
                  <tr key={a.resolver}>
                    <td className="mono">{a.resolver}</td>
                    <td className="muted">{a.via === 'adapter-dns' ? L('адаптер', 'adapter') : L('публичный', 'public')}</td>
                    <td className={a.ok ? '' : 'bad'}>{a.ok ? a.addresses[0] ?? L('нет A-записи', 'no A record') : a.error}</td>
                    <td className="r num">{a.ok ? `${ms(a.ms)} ${L('мс', 'ms')}` : ''}</td>
                  </tr>
                ))}
            </tbody>
          </table>
        )}
      </section>

      <section>
        <h3>{L('Источник', 'Source')}</h3>
        <dl>
          <Row k={L('Адаптер', 'Adapter')} v={adapter ? `${adapter.name} (${adapter.description})` : L('Авто, по таблице маршрутизации', 'Auto, by the routing table')} />
          <Row k={L('Адрес', 'Address')} v={session?.sourceIp} mono />
          <Row k={L('Шлюз', 'Gateway')} v={adapter?.gateways.join(', ') || null} mono />
          <Row k="DNS" v={adapter?.dns.join(', ') || null} mono />
          <Row k={L('Публичный IP', 'Public IP')} v={origin?.publicIp ? `${origin.publicIp}${origin.via ? ` (${L('через', 'via')} ${origin.via})` : ''}` : null} mono />
          <Row k={L('Провайдер', 'Provider')} v={origin?.asOrg ? `${origin.asOrg}${origin.asn ? ` · AS${origin.asn}` : ''}` : null} />
          <Row k={L('Место', 'Location')} v={origin?.city ? [origin.city, origin.region, countryName(origin.country)].filter(Boolean).join(', ') : null} />
          <Row k="Cloudflare" v={origin?.colo ? `${L('дата-центр', 'data centre')} ${origin.colo}${origin.coloCity ? ` (${origin.coloCity})` : ''}` : null} />
          <Row k={L('Источник', 'Source')} v={origin?.source ? `${origin.source}, ${origin.fetchedAt ? new Date(origin.fetchedAt).toLocaleTimeString(locale()) : ''}` : null} />
          {vpn && (
            <>
              <Row k={L('Выход VPN', 'VPN exit')} v={vpn.exit?.ip ? `${vpn.exit.ip} · ${vpn.exit.asOrg ?? ''}` : null} mono />
              <Row k={L('Место выхода', 'Exit location')} v={vpn.exit?.city ? `${vpn.exit.city}, ${countryName(vpn.exit.country) ?? ''}${vpn.exit.colo ? ` · Cloudflare ${vpn.exit.colo}` : ''}` : null} />
            </>
          )}
        </dl>
        <OriginEditor />
        <VpnServerEditor />
        <h3 className="sub">Path MTU</h3>
        <p className="sec-text">{L('Наибольший пакет, который проходит до цели без фрагментации. Меньше 1500 обычно значит туннель, PPPoE или VPN.', 'The largest packet that reaches the target without fragmentation. Below 1500 usually means a tunnel, PPPoE or VPN.')}</p>
        <div className="sec-actions">
          <button className="btn sm" onClick={runPmtu} disabled={pmtu.busy || !session?.targetIp}>
            {pmtu.busy ? L('Ищем…', 'Searching…') : L('Определить MTU', 'Find MTU')}
          </button>
          {pmtu.mtu !== undefined && !pmtu.busy && (
            <span className="pmtu num">{pmtu.mtu ? `${pmtu.mtu} ${L('байт', 'bytes')}` : L('цель не отвечает на ICMP с DF', 'the target does not answer ICMP with DF')}</span>
          )}
        </div>
      </section>

      <section>
        <h3>{L('Разбор маршрута', 'Route analysis')}</h3>
        {notes.length === 0 ? (
          <p className="sec-text">{L('Выводы появятся после 10 раундов: где начинаются потери, какие хопы просто режут ICMP, где растёт задержка.', 'Findings appear after 10 rounds: where loss starts, which hops merely limit ICMP, where latency grows.')}</p>
        ) : (
          <ul className="notes">
            {notes.map((n, i) => (
              <li key={i} className={n.level}>
                {n.text}
              </li>
            ))}
          </ul>
        )}
        <h3 className="sub">{L('Сессия', 'Session')}</h3>
        <dl>
          <Row k={L('Начало', 'Started')} v={session?.startedAt ? new Date(session.startedAt).toLocaleString(locale()) : null} />
          <Row k={L('Длительность', 'Duration')} v={elapsed ? duration(elapsed) : null} />
          <Row k={L('Раундов', 'Rounds')} v={round ? String(round) : null} />
          <Row k={L('Пакетов', 'Packets')} v={sent ? sent.toLocaleString(locale()) : null} />
        </dl>
        <div className="sec-actions">
          <button className="btn sm" onClick={() => request('shell.open', { path: 'sessions' })}>
            <Folder size={13} /> {L('Логи сессий (NDJSON)', 'Session logs (NDJSON)')}
          </button>
        </div>
      </section>

      <section>
        <h3>{L('Геолокация', 'Geolocation')}</h3>
        <dl>
          <Row
            k={L('Офлайн-база', 'Offline database')}
            v={
              geo
                ? geo.db === 'ready'
                  ? `${L('загружена', 'loaded')}: ${geo.active === 'custom' ? geo.dbDate : `DB-IP Lite ${geo.dbDate}`}`
                  : geo.db === 'downloading' || geo.db === 'updating'
                    ? `${L('загрузка', 'downloading')} ${Math.round(geo.progress * 100)}% (${geo.stage ?? ''})`
                    : geo.db === 'error'
                      ? `${L('ошибка', 'error')}: ${geo.error}`
                      : L('не загружена', 'not loaded')
                : null
            }
          />
          <Row k={L('Онлайн', 'Online')} v={options.geoOnline ? `${onlineList(providers)}${geo?.onlineError ? `; ${L('ошибка ip-api', 'ip-api error')}: ${geo.onlineError}` : ''}` : L('выключено: только офлайн-база', 'off: offline database only')} />
        </dl>
        <p className="sec-text">
          {L('Место каждого узла проверяется по скорости света: 1 мс RTT это не больше ~100 км. Источник, который противоречит задержке, отбрасывается. Узел, место которого не сошлось ни с одним источником, всё равно ставится на карту, но помечается как неподтверждённый. Сплошная точка: подтверждено несколькими источниками или RIPE IPmap; кольцо: один источник.', 'Every router location is checked against the speed of light: 1 ms of RTT is at most ~100 km. A source that contradicts the latency is discarded. A router whose location matches no source is still put on the map but marked as unconfirmed. Solid dot: confirmed by several sources or RIPE IPmap; ring: a single source.')}
        </p>
        <div className="sec-actions">
          {geo?.db === 'error' || geo?.db === 'missing' ? (
            <button className="btn sm" onClick={() => request('geo.download', {})}>
              {L('Скачать базу', 'Download database')}
            </button>
          ) : null}
          <button className="btn sm" onClick={() => openSettings('geo')}>
            {L('Базы и источники…', 'Databases and sources…')}
          </button>
          <button className={`btn sm ${options.geoOnline ? 'active' : ''}`} onClick={() => setOptions({ geoOnline: !options.geoOnline })}>
            {L('Онлайн-уточнение', 'Online refinement')}
          </button>
        </div>
      </section>
    </div>
  );
}

function OriginEditor() {
  const setting = useStore((s) => s.originSetting);
  const note = useStore((s) => s.routeGeo?.originNote ?? s.origin?.note ?? null);
  const setOrigin = useStore((s) => s.setOrigin);
  const [cities, setCities] = useState<{ city: string; cc: string; lat: number; lon: number }[]>([]);
  useEffect(() => {
    void request('geo.cities').then(setCities).catch(() => undefined);
  }, []);
  return (
    <>
      <h3 className="sub">{L('Ваше местоположение', 'Your location')}</h3>
      {note && <p className="sec-text warn-text">{note}</p>}
      <p className="sec-text">{L('Точка отсчёта для проверки по скорости света. Авто: по публичному IP физического адаптера через Cloudflare (запрос уходит с вашего реального IP). Вручную: точнее и без запросов.', 'The starting point for the speed-of-light check. Auto: by the public IP of the physical adapter via Cloudflare (the request leaves from your real IP). Manual: more precise and no requests.')}</p>
      <div className="sec-actions">
        <div className="seg">
          <button className={setting.mode === 'auto' ? 'on' : ''} onClick={() => setOrigin({ mode: 'auto', lat: null, lon: null, label: null })}>
            {L('Авто', 'Auto')}
          </button>
          <button className={setting.mode === 'manual' ? 'on' : ''} onClick={() => cities[0] && setOrigin({ mode: 'manual', lat: setting.lat ?? cities[0].lat, lon: setting.lon ?? cities[0].lon, label: setting.label ?? cities[0].city })}>
            {L('Вручную', 'Manual')}
          </button>
        </div>
        {setting.mode === 'manual' && (
          <select
            className="field sm"
            value={setting.label ?? ''}
            onChange={(e) => {
              const c = cities.find((x) => x.city === e.target.value);
              if (c) void setOrigin({ mode: 'manual', lat: c.lat, lon: c.lon, label: c.city });
            }}
          >
            {cities.map((c) => (
              <option key={c.city} value={c.city}>
                {c.city}, {c.cc}
              </option>
            ))}
          </select>
        )}
      </div>
    </>
  );
}

function VpnServerEditor() {
  const server = useStore((s) => s.vpnServer);
  const setServer = useStore((s) => s.setVpnServer);
  const tunnels = useStore((s) => s.tunnels);
  const [text, setText] = useState(server ?? '');
  if (tunnels.length === 0 && !server) return null;
  const detected = tunnels.flatMap((t) => t.servers.map((x) => `${x.ip}:${x.port} (${t.process ?? t.adapterName}, ${x.connections} ${L('соед.', 'conn.')})`));
  return (
    <>
      <h3 className="sub">{L('Сервер VPN', 'VPN server')}</h3>
      <p className="sec-text">{detected.length ? `${L('Найден автоматически', 'Found automatically')}: ${detected[0]}.` : L('Автоматически не найден (UDP-протокол или клиент не распознан).', 'Not found automatically (UDP protocol or unknown client).')}{' '}{L('Если определился неверно, укажите IP сервера из настроек VPN-клиента.', 'If it is wrong, enter the server IP from your VPN client\'s settings.')}</p>
      <div className="sec-actions">
        <input className="field sm mono" placeholder={L('авто', 'auto')} value={text} onChange={(e) => setText(e.target.value)} style={{ width: 180 }} />
        <button className="btn sm" onClick={() => setServer(text.trim() || null)}>
          {L('Сохранить', 'Save')}
        </button>
      </div>
    </>
  );
}

function onlineList(p: { ripeIpmap: boolean; ripeStat: boolean; cloudflareSelf: boolean; cloudflareColo: boolean; ipApi: boolean; teamCymru: boolean }) {
  const names = [p.ripeIpmap && 'RIPE IPmap', p.ripeStat && 'RIPEstat (MaxMind GeoLite)', (p.cloudflareSelf || p.cloudflareColo) && 'Cloudflare', p.ipApi && 'ip-api.com', p.teamCymru && 'Team Cymru'].filter(Boolean);
  return names.length ? names.join(', ') : L('все источники выключены', 'all sources disabled');
}

function Row({ k, v, mono }: { k: string; v: string | null | undefined; mono?: boolean }) {
  return (
    <div className="drow">
      <dt>{k}</dt>
      <dd className={`${mono ? 'mono' : ''} selectable`}>{v || <span className="muted">—</span>}</dd>
    </div>
  );
}
