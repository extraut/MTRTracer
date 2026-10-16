import { useEffect, useState } from 'react';
import { request } from '../bridge/client';
import type { ResolveAttempt } from '../bridge/protocol';
import { useStore } from '../store';
import { countryName, duration, ms } from '../lib/format';
import { Folder } from './Icons';

const viaText: Record<string, string> = {
  literal: 'IP-адрес, DNS не нужен',
  system: 'системный резолвер',
  'adapter-dns': 'DNS-сервер адаптера (фолбэк)',
  'public-dns': 'публичный DNS (фолбэк)',
  none: 'не удалось',
};

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
        <h3>Цель</h3>
        <dl>
          <Row k="Ввод" v={session?.input} mono />
          <Row k="Хост" v={resolved?.host ?? session?.host} mono />
          <Row k="IP" v={resolved?.addresses.length ? resolved.addresses.join(', ') : session?.targetIp} mono />
          <Row k="Резолв" v={resolved ? `${viaText[resolved.via] ?? resolved.via}${resolved.ms ? `, ${ms(resolved.ms)} мс` : ''}` : null} />
          {session?.port && <Row k="Порт" v={String(session.port)} mono />}
        </dl>
        {resolved && resolved.attempts.length > 1 && (
          <table className="mini">
            <tbody>
              {resolved.attempts.map((a, i) => (
                <tr key={i}>
                  <td className="mono">{a.resolver}</td>
                  <td className={a.ok ? 'ok' : 'bad'}>{a.ok ? 'ответ' : a.error}</td>
                  <td className="r num">{ms(a.ms)} мс</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        <div className="sec-actions">
          <button className="btn sm" onClick={runBench} disabled={bench.busy}>
            {bench.busy ? 'Опрашиваем DNS…' : 'Сравнить DNS-серверы'}
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
                    <td className="muted">{a.via === 'adapter-dns' ? 'адаптер' : 'публичный'}</td>
                    <td className={a.ok ? '' : 'bad'}>{a.ok ? a.addresses[0] ?? 'нет A-записи' : a.error}</td>
                    <td className="r num">{a.ok ? `${ms(a.ms)} мс` : ''}</td>
                  </tr>
                ))}
            </tbody>
          </table>
        )}
      </section>

      <section>
        <h3>Источник</h3>
        <dl>
          <Row k="Адаптер" v={adapter ? `${adapter.name} (${adapter.description})` : 'Авто, по таблице маршрутизации'} />
          <Row k="Адрес" v={session?.sourceIp} mono />
          <Row k="Шлюз" v={adapter?.gateways.join(', ') || null} mono />
          <Row k="DNS" v={adapter?.dns.join(', ') || null} mono />
          <Row k="Публичный IP" v={origin?.publicIp ? `${origin.publicIp}${origin.via ? ` (через ${origin.via})` : ''}` : null} mono />
          <Row k="Провайдер" v={origin?.asOrg ? `${origin.asOrg}${origin.asn ? ` · AS${origin.asn}` : ''}` : null} />
          <Row k="Место" v={origin?.city ? [origin.city, origin.region, countryName(origin.country)].filter(Boolean).join(', ') : null} />
          <Row k="Cloudflare" v={origin?.colo ? `дата-центр ${origin.colo}${origin.coloCity ? ` (${origin.coloCity})` : ''}` : null} />
          <Row k="Источник" v={origin?.source ? `${origin.source}, ${origin.fetchedAt ? new Date(origin.fetchedAt).toLocaleTimeString('ru-RU') : ''}` : null} />
          {vpn && (
            <>
              <Row k="Выход VPN" v={vpn.exit?.ip ? `${vpn.exit.ip} · ${vpn.exit.asOrg ?? ''}` : null} mono />
              <Row k="Место выхода" v={vpn.exit?.city ? `${vpn.exit.city}, ${countryName(vpn.exit.country) ?? ''}${vpn.exit.colo ? ` · Cloudflare ${vpn.exit.colo}` : ''}` : null} />
            </>
          )}
        </dl>
        <OriginEditor />
        <VpnServerEditor />
        <h3 className="sub">Path MTU</h3>
        <p className="sec-text">Наибольший пакет, который проходит до цели без фрагментации. Меньше 1500 обычно значит туннель, PPPoE или VPN.</p>
        <div className="sec-actions">
          <button className="btn sm" onClick={runPmtu} disabled={pmtu.busy || !session?.targetIp}>
            {pmtu.busy ? 'Ищем…' : 'Определить MTU'}
          </button>
          {pmtu.mtu !== undefined && !pmtu.busy && (
            <span className="pmtu num">{pmtu.mtu ? `${pmtu.mtu} байт` : 'цель не отвечает на ICMP с DF'}</span>
          )}
        </div>
      </section>

      <section>
        <h3>Разбор маршрута</h3>
        {notes.length === 0 ? (
          <p className="sec-text">Выводы появятся после 10 раундов: где начинаются потери, какие хопы просто режут ICMP, где растёт задержка.</p>
        ) : (
          <ul className="notes">
            {notes.map((n, i) => (
              <li key={i} className={n.level}>
                {n.text}
              </li>
            ))}
          </ul>
        )}
        <h3 className="sub">Сессия</h3>
        <dl>
          <Row k="Начало" v={session?.startedAt ? new Date(session.startedAt).toLocaleString('ru-RU') : null} />
          <Row k="Длительность" v={elapsed ? duration(elapsed) : null} />
          <Row k="Раундов" v={round ? String(round) : null} />
          <Row k="Пакетов" v={sent ? sent.toLocaleString('ru-RU') : null} />
        </dl>
        <div className="sec-actions">
          <button className="btn sm" onClick={() => request('shell.open', { path: 'sessions' })}>
            <Folder size={13} /> Логи сессий (NDJSON)
          </button>
        </div>
      </section>

      <section>
        <h3>Геолокация</h3>
        <dl>
          <Row
            k="DB-IP Lite"
            v={
              geo
                ? geo.db === 'ready'
                  ? `загружена, выпуск ${geo.dbDate}`
                  : geo.db === 'downloading' || geo.db === 'updating'
                    ? `загрузка ${Math.round(geo.progress * 100)}% (${geo.stage ?? ''})`
                    : geo.db === 'error'
                      ? `ошибка: ${geo.error}`
                      : 'не загружена'
                : null
            }
          />
          <Row k="Онлайн" v={options.geoOnline ? `RIPE IPmap, MaxMind GeoLite (RIPEstat), Cloudflare, ip-api.com${geo?.onlineError ? `; ошибка ip-api: ${geo.onlineError}` : ''}` : 'выключено: только офлайн-база'} />
        </dl>
        <p className="sec-text">
          Место каждого узла проверяется по скорости света: 1 мс RTT это не больше ~100 км. Источник, который противоречит задержке, отбрасывается, а узел без подтверждённого места на карту не ставится. Сплошная точка: подтверждено несколькими источниками или RIPE IPmap; кольцо: один источник.
        </p>
        <div className="sec-actions">
          {geo?.db === 'error' || geo?.db === 'missing' ? (
            <button className="btn sm" onClick={() => request('geo.download')}>
              Скачать базу
            </button>
          ) : null}
          <button className={`btn sm ${options.geoOnline ? 'active' : ''}`} onClick={() => setOptions({ geoOnline: !options.geoOnline })}>
            Онлайн-уточнение
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
      <h3 className="sub">Ваше местоположение</h3>
      {note && <p className="sec-text warn-text">{note}</p>}
      <p className="sec-text">Точка отсчёта для проверки по скорости света. Авто: по публичному IP физического адаптера через Cloudflare (запрос уходит с вашего реального IP). Вручную: точнее и без запросов.</p>
      <div className="sec-actions">
        <div className="seg">
          <button className={setting.mode === 'auto' ? 'on' : ''} onClick={() => setOrigin({ mode: 'auto', lat: null, lon: null, label: null })}>
            Авто
          </button>
          <button className={setting.mode === 'manual' ? 'on' : ''} onClick={() => cities[0] && setOrigin({ mode: 'manual', lat: setting.lat ?? cities[0].lat, lon: setting.lon ?? cities[0].lon, label: setting.label ?? cities[0].city })}>
            Вручную
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
  const detected = tunnels.flatMap((t) => t.servers.map((x) => `${x.ip}:${x.port} (${t.process ?? t.adapterName}, ${x.connections} соед.)`));
  return (
    <>
      <h3 className="sub">Сервер VPN</h3>
      <p className="sec-text">{detected.length ? `Найден автоматически: ${detected[0]}.` : 'Автоматически не найден (UDP-протокол или клиент не распознан).'} Если определился неверно, укажите IP сервера из настроек VPN-клиента.</p>
      <div className="sec-actions">
        <input className="field sm mono" placeholder="авто" value={text} onChange={(e) => setText(e.target.value)} style={{ width: 180 }} />
        <button className="btn sm" onClick={() => setServer(text.trim() || null)}>
          Сохранить
        </button>
      </div>
    </>
  );
}

function Row({ k, v, mono }: { k: string; v: string | null | undefined; mono?: boolean }) {
  return (
    <div className="drow">
      <dt>{k}</dt>
      <dd className={`${mono ? 'mono' : ''} selectable`}>{v || <span className="muted">—</span>}</dd>
    </div>
  );
}
