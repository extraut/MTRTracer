// Simulated host for running the UI in a plain browser (vite dev). Mirrors the C# host closely enough to
// exercise every screen: adapters and tunnels, MTR with loss/jitter/ECMP, the geo solver (including wrong
// database candidates and anycast), VPN mode, history, connections. Never touches the network.
import { L } from '../i18n';
import {
  defaultOptions,
  defaultProviders,
  defaultUrlTemplate,
  type GeoDbRelease,
  type GeoStatus,
  type AdapterInfo,
  type AppSettings,
  type CandidateVerdict,
  type Connection,
  type GeoCandidate,
  type GeoPoint,
  type Hop,
  type HopGeo,
  type HostInfo,
  type OriginInfo,
  type ProbeRow,
  type RouteGeo,
  type RunSummary,
  type TraceOptions,
  type TraceState,
  type TunnelInfo,
  type VpnBlock,
} from './protocol';

type Handler = (message: unknown) => void;
type Cand = [source: string, lat: number, lon: number, city: string, cc: string];

interface SimHop {
  ips: string[];
  host: (string | null)[];
  base: number;
  spread: number;
  loss: number;
  silent?: boolean;
  cands?: Cand[];
  asn?: [number, string];
  kind?: HostInfo['kind'];
  anycast?: boolean;
}

const HOME = { lat: 52.2297, lon: 21.0122, label: L('Варшава', 'Warsaw'), source: 'cloudflare' } satisfies GeoPoint;

const isp: SimHop[] = [
  { ips: ['10.0.0.1'], host: ['router.lan'], base: 0.9, spread: 0.4, loss: 0, kind: 'private' },
  { ips: ['198.51.100.10'], host: ['gw1.waw.example-isp.net'], base: 4.2, spread: 2.2, loss: 0, asn: [64500, 'Example ISP'], cands: [['hint', 52.23, 21.01, L('Варшава', 'Warsaw'), 'PL'], ['maxmind', 52.23, 21.02, 'Warsaw', 'PL'], ['dbip', 52.23, 21.01, 'Warsaw', 'PL']] },
  { ips: ['100.64.12.1'], host: [null], base: 5.1, spread: 1.6, loss: 0, kind: 'cgnat' },
  { ips: ['198.51.100.20'], host: ['ae7.waw-bb1.example-backbone.net'], base: 6.8, spread: 1.5, loss: 0.32, asn: [64501, 'Example Backbone'], cands: [['hint', 52.23, 21.01, L('Варшава', 'Warsaw'), 'PL'], ['dbip', 52.23, 21.01, 'Warsaw', 'PL']] },
  { ips: ['0.0.0.0'], host: [null], base: 0, spread: 0, loss: 1, silent: true },
  { ips: ['198.51.100.30'], host: ['ber-ix.example-backbone.net'], base: 12.4, spread: 1.8, loss: 0, asn: [64501, 'Example Backbone'], cands: [['hint', 52.52, 13.40, L('Берлин', 'Berlin'), 'DE'], ['maxmind', 52.23, 21.01, 'Warsaw', 'PL']] },
];

function route(target: string, ip: string): SimHop[] {
  if (/google|youtube|8\.8/.test(target))
    return [
      ...isp,
      { ips: ['62.115.137.38', '62.115.137.40'], host: ['sto-b2-link.ip.twelve99.net', 'sto-b2-link2.ip.twelve99.net'], base: 31, spread: 1.5, loss: 0, asn: [1299, 'Arelion'], cands: [['ripe-ipmap', 59.33, 18.07, 'Stockholm', 'SE'], ['hint', 59.33, 18.07, L('Стокгольм', 'Stockholm'), 'SE'], ['dbip', 59.33, 18.07, 'Stockholm', 'SE']] },
      // DB-IP puts this backbone router at the operator's US address: impossible at 39 ms from the user.
      { ips: ['62.115.124.118'], host: ['hbg-bb2-link.ip.twelve99.net'], base: 39, spread: 2, loss: 0.25, asn: [1299, 'Arelion'], cands: [['dbip', 39.04, -77.49, 'Ashburn', 'US'], ['ipapi', 40.71, -74.0, 'New York', 'US'], ['hint', 53.55, 9.99, L('Гамбург', 'Hamburg'), 'DE']] },
      { ips: ['62.115.114.89'], host: ['ffm-bb1-link.ip.twelve99.net'], base: 44, spread: 2, loss: 0, asn: [1299, 'Arelion'], cands: [['hint', 50.11, 8.68, L('Франкфурт', 'Frankfurt'), 'DE'], ['maxmind', 50.11, 8.68, 'Frankfurt am Main', 'DE']] },
      { ips: ['142.250.164.34'], host: [null], base: 45, spread: 1, loss: 0, asn: [15169, 'Google LLC'], cands: [['ripe-ipmap', 50.11, 8.68, 'Frankfurt', 'DE'], ['dbip', 37.42, -122.08, 'Mountain View', 'US']] },
      { ips: [ip], host: ['fra16s56-in-f14.1e100.net'], base: 45.5, spread: 1.2, loss: 0, asn: [15169, 'Google LLC'], cands: [['hint', 50.11, 8.68, L('Франкфурт', 'Frankfurt'), 'DE'], ['maxmind', 50.11, 8.68, 'Frankfurt am Main', 'DE'], ['dbip', 37.42, -122.08, 'Mountain View', 'US']] },
    ];
  // 1.1.1.1 and similar: anycast, answered by the nearest Cloudflare node; databases say USA / Australia.
  return [
    ...isp.slice(0, 4),
    { ips: ['188.254.2.6'], host: ['waw-ix.cloudflare.example'], base: 7.1, spread: 0.8, loss: 0, asn: [13335, 'Cloudflare'], cands: [['hint', 52.23, 21.01, L('Варшава', 'Warsaw'), 'PL'], ['dbip', 37.77, -122.42, 'San Francisco', 'US']], anycast: true },
    { ips: [ip], host: [/^\d/.test(target) ? 'one.one.one.one' : target], base: 7.4, spread: 0.9, loss: 0.01, asn: [13335, 'Cloudflare'], cands: [['dbip', 37.77, -122.42, 'San Francisco', 'US'], ['ipapi', -27.47, 153.02, 'Brisbane', 'AU']], anycast: true },
  ];
}

function vpnUnderlay(): SimHop[] {
  return [
    ...isp,
    { ips: ['198.51.100.60'], host: ['ams-ix.example-backbone.net'], base: 21, spread: 1.5, loss: 0, asn: [64501, 'Example Backbone'], cands: [['ripe-ipmap', 52.37, 4.9, 'Amsterdam', 'NL'], ['hint', 52.37, 4.9, L('Амстердам', 'Amsterdam'), 'NL']] },
    { ips: ['192.0.2.44'], host: ['vps44.example-hosting.net'], base: 22.5, spread: 1.4, loss: 0, asn: [64502, 'Example Hosting'], cands: [['maxmind', 52.37, 4.9, 'Amsterdam', 'NL'], ['ripe-ipmap', 52.35, 4.92, 'Amsterdam', 'NL']] },
  ];
}

// ---- same rules as RouteGeoSolver.cs ----
const hav = (a: { lat: number; lon: number }, b: { lat: number; lon: number }) => {
  const r = (d: number) => (d * Math.PI) / 180;
  const h = Math.sin(r(b.lat - a.lat) / 2) ** 2 + Math.cos(r(a.lat)) * Math.cos(r(b.lat)) * Math.sin(r(b.lon - a.lon) / 2) ** 2;
  return 2 * 6371 * Math.asin(Math.min(1, Math.sqrt(h)));
};
const weight: Record<string, number> = { 'ripe-ipmap': 6, 'cf-colo': 6, hint: 5, maxmind: 3, dbip: 2, ipapi: 1.5 };
const label: Record<string, string> = { 'ripe-ipmap': 'RIPE IPmap', hint: L('имя роутера', 'router name'), maxmind: 'MaxMind GeoLite (RIPEstat)', dbip: 'DB-IP Lite', ipapi: 'ip-api.com' };

function solve(sessionId: string, origin: GeoPoint, hops: Hop[], hosts: Map<string, HostInfo>): RouteGeo {
  const eff: (number | null)[] = [];
  let run: number | null = null;
  for (let i = hops.length - 1; i >= 0; i--) {
    const h = hops[i];
    if (h.best != null && h.recv >= 2) run = run == null ? h.best : Math.min(run, h.best);
    eff[i] = run;
  }
  const out: HopGeo[] = [];
  let prev: { lat: number; lon: number; label: string } | null = null;
  hops.forEach((h, i) => {
    const limit = eff[i] != null ? eff[i]! * 100 + 60 : null;
    const base = { ttl: h.ttl, ip: h.primary, maxDistanceKm: limit, source: null, city: null, countryCode: null } as const;
    if (!h.primary) return out.push({ ...base, lat: null, lon: null, status: 'silent', reason: null, verdicts: [] });
    const info = hosts.get(h.primary);
    if (!info) return out.push({ ...base, lat: null, lon: null, status: 'pending', reason: null, verdicts: [] });
    if (info.kind !== 'public') return out.push({ ...base, lat: origin.lat, lon: origin.lon, status: 'local', reason: L('адрес локальной сети или провайдера, место совпадает с вашим', 'local network or provider address, same location as yours'), verdicts: [] });
    const verdicts: CandidateVerdict[] = [];
    const ok: GeoCandidate[] = [];
    for (const c of info.candidates) {
      let reason: string | null = null;
      const d = hav(origin, c);
      if (limit != null && d > limit) reason = L(`${Math.round(d)} км от вас, а RTT ${eff[i]!.toFixed(1)} мс допускает не больше ${Math.round(limit)} км`, `${Math.round(d)} km from you, but an RTT of ${eff[i]!.toFixed(1)} ms allows at most ${Math.round(limit)} km`);
      if (!reason && prev && eff[i] != null) {
        const det = hav(origin, prev) + hav(prev, c);
        const allowed = eff[i]! * 100 + 180;
        if (det > allowed) reason = L(`путь через предыдущий узел (${prev.label}) ${Math.round(det)} км, а RTT ${eff[i]!.toFixed(1)} мс допускает ${Math.round(allowed)} км`, `the path via the previous router (${prev.label}) is ${Math.round(det)} km, but an RTT of ${eff[i]!.toFixed(1)} ms allows ${Math.round(allowed)} km`);
      }
      if (!reason) ok.push(c);
      verdicts.push({ source: c.source, lat: c.lat, lon: c.lon, city: c.city, countryCode: c.countryCode, level: c.level, fetchedAt: c.fetchedAt, accepted: !reason, reason });
    }
    if (ok.length) {
      let best = ok[0];
      let bestScore = -1e9;
      let agreeN = 0;
      for (const c of ok) {
        const agree = ok.filter((x) => x !== c && x.source !== c.source && hav(x, c) <= 120).length;
        const s = (weight[c.source] ?? 1) + agree * 1.5;
        if (s > bestScore) [best, bestScore, agreeN] = [c, s, agree];
      }
      const why = agreeN > 0 ? L(`${label[best.source]}, совпадает ещё с ${agreeN} ${agreeN === 1 ? 'источником' : 'источниками'}`, `${label[best.source]}, agrees with ${agreeN} more ${agreeN === 1 ? 'source' : 'sources'}`) : label[best.source];
      prev = { lat: best.lat, lon: best.lon, label: best.city ?? h.primary };
      return out.push({
        ...base, lat: best.lat, lon: best.lon, status: agreeN > 0 || best.source === 'ripe-ipmap' ? 'confirmed' : 'single', source: best.source, city: best.city, countryCode: best.countryCode, reason: why,
        verdicts: verdicts.map((v) => (v.source === best.source ? { ...v, reason: L('выбран: ', 'chosen: ') + why } : v)),
      });
    }
    if (info.anycast) {
      const at = prev ?? origin;
      return out.push({ ...base, lat: at.lat, lon: at.lon, status: 'anycast', reason: L('anycast: адрес обслуживают узлы во многих городах, отвечает ближайший. Координаты из баз указывают на регистрацию, а не на ответивший узел', 'anycast: the address is served from many cities and the nearest one answers. Database coordinates point to the registration, not to the node that answered'), verdicts });
    }
    const f = [...info.candidates].sort((a, b) => (weight[b.source] ?? 1) - (weight[a.source] ?? 1))[0];
    if (!f) return out.push({ ...base, lat: null, lon: null, status: 'unresolved', reason: L('нет данных о местоположении', 'no location data'), verdicts });
    return out.push({ ...base, lat: f.lat, lon: f.lon, status: 'unresolved', source: f.source, city: f.city, countryCode: f.countryCode, reason: L(`${label[f.source]}: место не сходится с измеренной задержкой, показано как есть`, `${label[f.source]}: the location does not match the measured latency, shown as is`), verdicts });
  });
  let path = 0;
  let last: { lat: number; lon: number } = origin;
  for (const g of out) {
    if (g.lat == null || !['confirmed', 'single', 'anycast', 'unresolved'].includes(g.status)) continue;
    path += hav(last, { lat: g.lat, lon: g.lon! });
    last = { lat: g.lat, lon: g.lon! };
  }
  const destHop = hops.find((h) => h.isDestination);
  const dest = destHop ? out.find((g) => g.ttl === destHop.ttl) : undefined;
  const direct = dest && dest.lat != null && (dest.status === 'confirmed' || dest.status === 'single') ? hav(origin, { lat: dest.lat, lon: dest.lon! }) : null;
  return {
    sessionId, origin, hops: out,
    summary: { pathKm: path, directKm: direct, minRttMs: direct != null ? direct / 100 : null, measuredMs: destHop?.best ?? null, stretch: direct != null && destHop?.best ? destHop.best / (direct / 100) : null, unresolved: out.filter((g) => g.status === 'unresolved').length },
  };
}

class Stats {
  sent = 0;
  recv = 0;
  last: number | null = null;
  best: number | null = null;
  worst: number | null = null;
  mean = 0;
  m2 = 0;
  logSum = 0;
  prev: number | null = null;
  jSum = 0;
  jN = 0;
  jitter: number | null = null;
  jMax: number | null = null;
  jInt: number | null = null;
  history: (number | null)[] = [];
  consecutiveLoss = 0;
  reply(r: number) {
    this.recv++;
    this.last = r;
    this.best = this.best == null ? r : Math.min(this.best, r);
    this.worst = this.worst == null ? r : Math.max(this.worst, r);
    const d = r - this.mean;
    this.mean += d / this.recv;
    this.m2 += d * (r - this.mean);
    this.logSum += Math.log(Math.max(r, 0.001));
    if (this.prev != null) {
      const j = Math.abs(r - this.prev);
      this.jitter = j;
      this.jSum += j;
      this.jN++;
      this.jMax = this.jMax == null ? j : Math.max(this.jMax, j);
      this.jInt = this.jInt == null ? j : this.jInt + (j - this.jInt) / 16;
    }
    this.prev = r;
    this.consecutiveLoss = 0;
    this.push(r);
  }
  loss() {
    this.consecutiveLoss++;
    this.push(null);
  }
  push(v: number | null) {
    this.history.push(v);
    if (this.history.length > 120) this.history.shift();
  }
}

const r3 = (v: number | null) => (v == null ? null : Math.round(v * 1000) / 1000);

const adapters: AdapterInfo[] = [
  {
    id: '{A1}', name: 'tun0', description: 'Xray Tunnel', kind: 'vpn', status: 'up', mac: null, speedBps: 100_000_000_000, mtu: 1500,
    ifIndex: 10, ipv6IfIndex: 10, ifType: 'Unknown', ipv4: [{ address: '10.8.0.2', prefix: 30 }], ipv6: [], gateways: [], dns: ['10.8.0.1'],
    dnsSuffix: null, dhcp: false, hasGateway: false, hasRoute: true, hasRouteV6: false, isWindowsChoice: true, isWindowsChoiceV6: false, routeMetric: 0,
    bytesSent: 1_204_000_000, bytesRecv: 9_880_000_000,
  },
  {
    id: '{B2}', name: 'Ethernet', description: 'PCIe Gigabit Ethernet Adapter', kind: 'ethernet', status: 'up', mac: '02:00:00:00:00:01',
    speedBps: 1_000_000_000, mtu: 1500, ifIndex: 7, ipv6IfIndex: 7, ifType: 'Ethernet', ipv4: [{ address: '10.0.0.2', prefix: 24 }],
    ipv6: [{ address: 'fe80::1c2b:3a4d:5e6f:7a8b%7', prefix: 64 }], gateways: ['10.0.0.1'], dns: ['10.0.0.1', '8.8.8.8'], dnsSuffix: 'lan',
    dhcp: true, hasGateway: true, hasRoute: true, hasRouteV6: false, isWindowsChoice: false, isWindowsChoiceV6: false, routeMetric: null,
    bytesSent: 3_400_000_000, bytesRecv: 41_200_000_000,
  },
  {
    id: '{C3}', name: 'vEthernet (Default Switch)', description: 'Hyper-V Virtual Ethernet Adapter', kind: 'virtual', status: 'up', mac: '02:00:00:00:00:02',
    speedBps: 10_000_000_000, mtu: 1500, ifIndex: 23, ipv6IfIndex: 23, ifType: 'Ethernet', ipv4: [{ address: '10.10.0.1', prefix: 20 }], ipv6: [],
    gateways: [], dns: [], dnsSuffix: null, dhcp: false, hasGateway: false, hasRoute: false, hasRouteV6: false, isWindowsChoice: false,
    isWindowsChoiceV6: false, routeMetric: null, bytesSent: 1_200_000, bytesRecv: 800_000,
  },
  {
    id: '{D4}', name: L('Беспроводная сеть', 'Wi-Fi'), description: 'Intel(R) Wi-Fi 6E AX211 160MHz', kind: 'wifi', status: 'down', mac: '02:00:00:00:00:03',
    speedBps: 0, mtu: 1500, ifIndex: 15, ipv6IfIndex: 15, ifType: 'Wireless80211', ipv4: [], ipv6: [], gateways: [], dns: [], dnsSuffix: null,
    dhcp: true, hasGateway: false, hasRoute: false, hasRouteV6: false, isWindowsChoice: false, isWindowsChoiceV6: false, routeMetric: null,
    bytesSent: 0, bytesRecv: 0,
  },
];

const tunnel: TunnelInfo = {
  adapterId: '{A1}', adapterName: 'tun0', isDefaultRoute: true, process: 'xray', pid: 4120, processPath: 'C:\\Tools\\xray\\xray.exe',
  servers: [{ ip: '192.0.2.44', port: 443, connections: 6 }, { ip: '149.154.167.51', port: 443, connections: 1 }],
  carrierId: '{B2}', carrierName: 'Ethernet', carrierIp: '10.0.0.2',
  explanation: L('Туннель «tun0» не имеет своего провода: зашифрованный трафик физически уходит через «Ethernet». Поэтому счётчики Ethernet всегда больше.', 'Tunnel "tun0" has no wire of its own: its encrypted traffic physically leaves through "Ethernet". That is why the Ethernet counters are always higher.'),
};

const processes = [
  ['chrome.exe', 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe'],
  ['Telegram.exe', 'C:\\Users\\user\\AppData\\Roaming\\Telegram Desktop\\Telegram.exe'],
  ['steam.exe', 'C:\\Program Files (x86)\\Steam\\steam.exe'],
  ['Discord.exe', 'C:\\Users\\user\\AppData\\Local\\Discord\\app-1.0.9\\Discord.exe'],
  ['svchost.exe', 'C:\\Windows\\System32\\svchost.exe'],
  ['xray.exe', 'C:\\Tools\\xray\\xray.exe'],
  ['Code.exe', 'C:\\Users\\user\\AppData\\Local\\Programs\\Microsoft VS Code\\Code.exe'],
];

const cities = [
  { city: L('Амстердам', 'Amsterdam'), cc: 'NL', lat: 52.37, lon: 4.9 },
  { city: L('Берлин', 'Berlin'), cc: 'DE', lat: 52.52, lon: 13.4 },
  { city: L('Варшава', 'Warsaw'), cc: 'PL', lat: 52.23, lon: 21.01 },
  { city: L('Прага', 'Prague'), cc: 'CZ', lat: 50.08, lon: 14.44 },
  { city: L('Франкфурт', 'Frankfurt'), cc: 'DE', lat: 50.11, lon: 8.68 },
];

function seedHistory(): RunSummary[] {
  const now = Date.now();
  const day = 86_400_000;
  const mk = (id: number, key: string, ago: number, avg: number, loss: number, mode = 'normal', adapter = 'Ethernet', hash = 'a1b2c3d4e5f6'): RunSummary => ({
    id, targetKey: key, input: key, targetIp: key === 'google.com' ? '142.250.185.78' : '1.1.1.1', mode, adapterName: adapter, sourceIp: adapter === 'Ethernet' ? '10.0.0.2' : '10.8.0.2',
    publicIp: '203.0.113.12', startedAt: now - ago, endedAt: now - ago + 600_000, snapshots: 10, loss, avg, best: avg - 1.2, jitter: 1.4, hops: 11, routeHash: hash,
  });
  return [
    mk(1, 'google.com', 2 * day + 3_600_000 * 5, 44.8, 0),
    mk(2, 'google.com', day + 3_600_000 * 2, 46.1, 0),
    mk(3, 'google.com', day - 3_600_000 * 6, 58.9, 1.5, 'normal', 'Ethernet', 'f0e1d2c3b4a5'),
    mk(4, 'google.com', 3_600_000 * 7, 45.3, 0),
    mk(5, 'google.com', 3_600_000 * 2, 63.4, 2.2, 'vpn', 'tun0'),
    mk(6, '1.1.1.1', day + 3_600_000 * 3, 7.4, 0),
    mk(7, '1.1.1.1', 3_600_000 * 4, 7.9, 0),
  ];
}

export function createMockHost() {
  const handlers: Handler[] = [];
  const emit = (type: string, payload: unknown) => setTimeout(() => handlers.forEach((h) => h({ kind: 'evt', type, payload })), 0);
  const stored = (() => {
    try {
      return JSON.parse(localStorage.getItem('mock.settings.v2') ?? 'null') as AppSettings | null;
    } catch {
      return null;
    }
  })();
  const settings: AppSettings = stored ?? {
    adapterId: null, adapterChosen: false, vpnMode: false, vpnServer: null, origin: { mode: 'auto', lat: null, lon: null, label: null },
    options: { ...defaultOptions }, recent: ['1.1.1.1', 'google.com', 'ya.ru'], ui: null,
    language: null, providers: { ...defaultProviders }, geoDb: { autoUpdate: true, active: null, urlTemplate: defaultUrlTemplate }, updateRepo: 'extraut/MTRTracer',
  };
  settings.providers ??= { ...defaultProviders };
  settings.geoDb ??= { autoUpdate: true, active: null, urlTemplate: defaultUrlTemplate };
  settings.updateRepo ??= 'extraut/MTRTracer';
  settings.language ??= null;
  const save = () => localStorage.setItem('mock.settings.v2', JSON.stringify(settings));
  const installed = new Set<string>(['2026-10', '2026-09']);
  const geo: GeoStatus = { db: 'ready', progress: 1, stage: null, dbDate: '2026-10', error: null, online: true, onlineError: null, active: '2026-10', enabled: settings.providers.offlineDb, autoUpdate: true };
  const cache = { hosts: 14, ripeIpmap: 212, ripeStatGeo: 188, ripeStatNet: 190, ipApi: 97 };
  const releases = (): GeoDbRelease[] =>
    [...installed]
      .sort()
      .reverse()
      .map((id) =>
        id === 'custom'
          ? { id, label: L('Свой файл', 'Imported file'), city: true, asn: false, bytes: 61_000_000, type: 'GeoLite2-City', built: '2026-10-07', active: geo.active === id }
          : { id, label: `DB-IP Lite ${id}`, city: true, asn: true, bytes: 131_000_000 + 9_000_000, type: 'DB-IP Lite', built: id, active: geo.active === id },
      );
  const simulateDownload = (month: string) => {
    geo.db = 'updating';
    geo.progress = 0;
    geo.stage = 'dbip-city-lite';
    const t = window.setInterval(() => {
      geo.progress = Math.min(1, geo.progress + 0.08);
      if (geo.progress >= 1) {
        window.clearInterval(t);
        installed.add(month);
        geo.db = 'ready';
        geo.stage = null;
        if (!settings.geoDb.active) {
          geo.active = [...installed].filter((m) => m !== 'custom').sort().at(-1) ?? month;
          geo.dbDate = geo.active;
        }
      }
      emit('geo.status', { ...geo });
    }, 200);
  };
  const runs = seedHistory();
  let runSeq = 100;

  let session: { stop: () => void; state: TraceState; paused: boolean; input: string; snapshot: () => Hop[]; runId: number | null } | null = null;
  let trafficTimer: number | undefined;
  let connTimer: number | undefined;
  let seq = 0;
  const hostInfos = new Map<string, HostInfo>();

  const origin = (): GeoPoint =>
    settings.origin.mode === 'manual' && settings.origin.lat != null
      ? { lat: settings.origin.lat, lon: settings.origin.lon!, label: settings.origin.label, source: 'manual' }
      : HOME;

  const originInfo = (): OriginInfo => ({
    mode: settings.origin.mode, manual: settings.origin.mode === 'manual' ? settings.origin : null, publicIp: '203.0.113.12', asn: 64500, asOrg: 'Example ISP',
    city: 'Warsaw', region: 'Mazovia', country: 'PL', lat: 52.2297, lon: 21.0122, colo: 'WAW', coloCity: 'Warsaw', via: 'Ethernet',
    source: 'Cloudflare (speed.cloudflare.com/meta)', fetchedAt: Date.now(), point: origin(),
  });

  const discover = (ip: string, h: SimHop) => {
    if (hostInfos.has(ip)) {
      emit('host.info', hostInfos.get(ip));
      return;
    }
    const idx = h.ips.indexOf(ip);
    const t = Date.now() - Math.round(Math.random() * 5) * 86_400_000;
    const info: HostInfo = {
      ip, kind: h.kind ?? 'public', hostname: null, hostnameDone: false, asn: h.asn?.[0] ?? null, asName: h.asn?.[1] ?? null, asSource: h.asn ? 'RIPEstat (BGP)' : null,
      prefix: null, registry: null, isp: null, org: null, anycast: !!h.anycast, hintToken: null, pending: 3,
      candidates: (h.cands ?? []).filter((c) => c[0] !== 'hint').map(([source, lat, lon, city, cc]) => ({ source, lat, lon, city, region: null, countryCode: cc, level: 'city', fetchedAt: t, note: null })),
    };
    hostInfos.set(ip, info);
    emit('host.info', { ...info });
    setTimeout(() => {
      info.hostname = h.host[idx] ?? null;
      info.hostnameDone = true;
      info.isp = h.asn?.[1] ?? null;
      info.prefix = h.asn ? ip.split('.').slice(0, 3).join('.') + '.0/24' : null;
      info.registry = h.asn ? 'ripencc' : null;
      info.pending = 0;
      info.candidates = (h.cands ?? []).map(([source, lat, lon, city, cc]) => ({ source, lat, lon, city, region: null, countryCode: cc, level: 'city', fetchedAt: source === 'hint' ? Date.now() : t, note: null }));
      emit('host.info', { ...info, candidates: [...info.candidates] });
    }, 300 + Math.random() * 900);
  };

  const startSession = (input: string, opts: TraceOptions) => {
    session?.stop();
    if (session?.runId != null) {
      const r = runs.find((x) => x.id === session!.runId);
      if (r) r.endedAt = Date.now();
    }
    const id = `s${Date.now() % 100000}`;
    const host = input.replace(/^\w+:\/\//, '').replace(/[/:?#].*$/, '').toLowerCase();
    const isIp = /^\d+\.\d+\.\d+\.\d+$/.test(host);
    const targetIp = isIp ? host : /google|youtube/.test(host) ? '142.250.185.78' : host === 'blocked.example' ? '203.0.113.50' : '1.1.1.1';
    const adapter = adapters.find((a) => a.id === settings.adapterId) ?? adapters.find((a) => a.isWindowsChoice)!;
    const vpnMode = settings.vpnMode && adapter.kind === 'vpn';
    const fake = !vpnMode && adapter.kind === 'vpn';
    const underTarget = vpnMode ? tunnel.servers[0].ip : targetIp;
    const state: TraceState = {
      sessionId: id, state: 'resolving', input: vpnMode ? underTarget : input, host: vpnMode ? underTarget : host, targetIp: null, sourceIp: null, adapterId: settings.adapterId,
      family: null, startedAt: 0, error: null, errorCode: null, outageSince: null, port: null,
      mode: vpnMode ? 'vpn' : 'normal', overlayInput: vpnMode ? input : null, overlayIp: vpnMode ? targetIp : null, carrierName: vpnMode ? 'Ethernet' : null,
    };
    const sim = vpnMode ? vpnUnderlay() : fake ? [{ ips: [targetIp], host: [null], base: 0.12, spread: 0.05, loss: 0 } as SimHop] : route(host, targetIp);
    const blocked = host === 'blocked.example';
    if (blocked) sim[sim.length - 1].silent = true;
    const hops = sim.map(() => ({ agg: new Stats(), responders: new Map<string, Stats>(), status: 'pending' }));
    const overlay = new Stats();
    const tcp = new Stats();
    let round = 0;
    let timer: number | undefined;
    const started = performance.now();
    let stopped = false;
    let hopDtos: Hop[] = [];
    session = { stop: () => { stopped = true; clearInterval(timer); }, state, paused: false, input, snapshot: () => hopDtos, runId: null };
    const current = session;
    emit('trace.state', { ...state });

    setTimeout(() => {
      if (stopped) return;
      if (host === 'nonexistent.invalid') {
        state.state = 'error';
        state.errorCode = 'resolve-failed';
        state.error = L(`Не удалось получить IP для «${host}»: NXDOMAIN: домен не существует.`, `Could not resolve "${host}": NXDOMAIN: the domain does not exist.`);
        emit('trace.state', { ...state });
        emit('trace.event', { t: Date.now(), level: 'error', code: 'resolve-failed', message: state.error, ttl: null, ip: null });
        return;
      }
      emit('trace.resolved', { sessionId: id, input: state.input, host: state.host, address: underTarget, addresses: [underTarget], via: isIp || vpnMode ? 'literal' : 'system', ms: isIp ? 0 : 18.4, attempts: [] });
      state.state = 'running';
      state.targetIp = underTarget;
      state.sourceIp = vpnMode ? '10.0.0.2' : adapter.ipv4[0]?.address ?? null;
      state.family = '4';
      state.startedAt = Date.now();
      emit('trace.state', { ...state });
      emit('trace.event', { t: Date.now(), level: 'info', code: 'start', message: vpnMode ? L(`VPN-режим: хопы до сервера ${underTarget} через Ethernet, сквозная задержка до ${input} через tun0.`, `VPN mode: hops to server ${underTarget} via Ethernet, end-to-end latency to ${input} via tun0.`) : L(`Трассировка ${host} [${targetIp}] с ${state.sourceIp}: интервал ${opts.intervalMs} мс, ${opts.packetSize} байт.`, `Tracing ${host} [${targetIp}] from ${state.sourceIp}: interval ${opts.intervalMs} ms, ${opts.packetSize} bytes.`), ttl: null, ip: null });
      if (fake) emit('trace.event', { t: Date.now(), level: 'error', code: 'fake-replies', message: L(`Цель «ответила» на первом же хопе за 0.12 мс: это отвечает сам VPN-туннель, а не ${targetIp}. Включите VPN-режим.`, `The target "answered" on the very first hop in 0.12 ms: that is the VPN tunnel itself, not ${targetIp}. Turn on VPN mode.`), ttl: 1, ip: targetIp });
      sim.forEach((h) => h.ips.forEach((ip) => ip !== '0.0.0.0' && discover(ip, h)));
      if (vpnMode) discover(targetIp, route(host, targetIp).at(-1)!);
      const prev = runs.filter((r) => r.targetKey === host).sort((a, b) => b.startedAt - a.startedAt)[0];
      if (prev) emit('history.previous', { sessionId: id, run: prev });
      current.runId = ++runSeq;
      runs.push({ id: current.runId, targetKey: host, input, targetIp, mode: vpnMode ? 'vpn' : 'normal', adapterName: adapter.name, sourceIp: state.sourceIp, publicIp: '203.0.113.12', startedAt: Date.now(), endedAt: null, snapshots: 0, loss: null, avg: null, best: null, jitter: null, hops: sim.length, routeHash: 'a1b2c3d4e5f6' });

      const tick = () => {
        if (current.paused || stopped) return;
        round++;
        const rows: ProbeRow[] = [];
        sim.forEach((h, i) => {
          const st = hops[i];
          for (let b = 0; b < opts.burst; b++) {
            st.agg.sent++;
            const t = Date.now();
            const lost = h.silent || Math.random() < h.loss;
            const ip = h.ips[Math.random() < 0.7 ? 0 : h.ips.length - 1];
            const isDest = i === sim.length - 1;
            if (lost) {
              st.agg.loss();
              st.status = 'timeout';
              rows.push({ t, round, seq: ++seq, ttl: i + 1, src: state.sourceIp, dst: underTarget, from: null, status: 'timeout', rawStatus: 11010, rttMs: null, kernelMs: 0, size: opts.packetSize, proto: 'icmp' });
            } else {
              const spike = Math.random() < 0.04 ? Math.random() * 40 : 0;
              const rtt = Math.max(0.05, h.base + (Math.random() - 0.3) * h.spread + spike);
              st.agg.reply(rtt);
              let rs = st.responders.get(ip);
              if (!rs) st.responders.set(ip, (rs = new Stats()));
              rs.sent++;
              rs.reply(rtt);
              st.status = isDest ? 'ok' : 'ttl-expired';
              rows.push({ t, round, seq: ++seq, ttl: i + 1, src: state.sourceIp, dst: underTarget, from: ip, status: st.status, rawStatus: isDest ? 0 : 11013, rttMs: Math.round(rtt * 1000) / 1000, kernelMs: Math.round(rtt), size: opts.packetSize, proto: 'icmp' });
            }
          }
        });
        if (vpnMode) {
          overlay.sent++;
          if (Math.random() < 0.015) overlay.loss();
          else overlay.reply(/google/.test(host) ? 62 + Math.random() * 6 : 38 + Math.random() * 4);
        }
        if (blocked && round >= 4) {
          tcp.sent++;
          tcp.reply(46 + Math.random() * 3);
        }
        emit('trace.probes', { rows });

        hopDtos = sim.map((h, i) => {
          const st = hops[i];
          const a = st.agg;
          const total = Math.max(1, [...st.responders.values()].reduce((s, x) => s + x.recv, 0));
          const responders = [...st.responders.entries()].sort((x, y) => y[1].recv - x[1].recv).map(([ip, s]) => ({
            ip, count: s.recv, share: Math.round((s.recv * 1000) / total) / 10, avg: r3(s.mean), best: r3(s.best), last: r3(s.last),
          }));
          return {
            ttl: i + 1, primary: responders[0]?.ip ?? null, responders, sent: a.sent, recv: a.recv,
            loss: a.sent ? Math.round(((a.sent - a.recv) * 1000) / a.sent) / 10 : 0,
            last: r3(a.last), best: r3(a.best), avg: a.recv ? r3(a.mean) : null, worst: r3(a.worst),
            stDev: a.recv > 1 ? r3(Math.sqrt(a.m2 / (a.recv - 1))) : null, gMean: a.recv ? r3(Math.exp(a.logSum / a.recv)) : null,
            jitter: r3(a.jitter), jAvg: a.jN ? r3(a.jSum / a.jN) : null, jMax: r3(a.jMax), jInt: r3(a.jInt),
            status: h.silent ? 'timeout' : st.status, history: a.history, isDestination: !blocked && i === sim.length - 1, consecutiveLoss: a.consecutiveLoss,
          };
        });
        const dest = hopDtos[hopDtos.length - 1];
        const notes = [] as { level: 'ok' | 'info' | 'warn' | 'error'; code: string; text: string; ttl: number | null }[];
        if (fake) notes.push({ level: 'error', code: 'fake-replies', text: L('Ответы подделаны VPN-туннелем: задержки и хопы в таблице не настоящие. Для туннеля используйте VPN-режим.', 'Replies are faked by the VPN tunnel: latencies and hops in the table are not real. Use VPN mode for the tunnel.'), ttl: 1 });
        else if (round >= 10) {
          if (!blocked) notes.push({ level: 'ok', code: 'healthy', text: L(`Маршрут стабилен: ${dest.loss}% потерь, средняя задержка ${dest.avg?.toFixed(1)} мс.`, `Route is stable: ${dest.loss}% loss, ${dest.avg?.toFixed(1)} ms average latency.`), ttl: dest.ttl });
          notes.push({ level: 'info', code: 'rate-limit', text: L(`Хоп 4: ${hopDtos[3]?.loss ?? 0}% потерь, но дальше пакеты проходят. Роутер ограничивает ответы ICMP, на трафик это не влияет.`, `Hop 4: ${hopDtos[3]?.loss ?? 0}% loss, but packets get through further on. The router rate-limits ICMP replies; traffic is not affected.`), ttl: 4 });
        }
        const geo = solve(id, origin(), hopDtos, hostInfos);
        let vpn: VpnBlock | null = null;
        if (vpnMode) {
          const serverBest = dest.best;
          const tinfo = hostInfos.get(targetIp);
          const tg = tinfo ? solve(id, origin(), [{ ...dest, ttl: 0, primary: targetIp, best: overlay.best, isDestination: true }], hostInfos).hops[0] : null;
          vpn = {
            server: underTarget, serverPort: 443, process: 'xray', tunnel: 'tun0', carrier: 'Ethernet',
            exit: { ip: '192.0.2.44', asOrg: 'Example Hosting', asn: 64502, city: 'Amsterdam', country: 'NL', lat: 52.37, lon: 4.9, colo: 'AMS', coloCity: 'Amsterdam', coloLat: 52.31, coloLon: 4.76, fetchedAt: Date.now() },
            target: host, targetIp, port: 443, method: 'https', sent: overlay.sent, recv: overlay.recv,
            loss: overlay.sent ? Math.round(((overlay.sent - overlay.recv) * 1000) / overlay.sent) / 10 : 0,
            last: r3(overlay.last), best: r3(overlay.best), avg: overlay.recv ? r3(overlay.mean) : null, worst: r3(overlay.worst), jAvg: overlay.jN ? r3(overlay.jSum / overlay.jN) : null,
            setupMs: 71.4, connectFaked: true, serverBest, legMs: overlay.best != null && serverBest != null ? r3(overlay.best - serverBest) : null, status: 'https', history: overlay.history,
            targetGeo: tg && tg.status === 'anycast' ? { ...tg, lat: 52.31, lon: 4.76, city: L('Амстердам', 'Amsterdam'), reason: L('anycast: отвечает узел рядом с выходом VPN', 'anycast: the node near the VPN exit answers') } : tg,
          };
        }
        emit('trace.hops', {
          sessionId: id, round, destTtl: blocked ? null : sim.length, hops: hopDtos, notes, elapsedMs: Math.round(performance.now() - started), vpn,
          tcp: blocked && tcp.sent ? { port: opts.tcpPort, sent: tcp.sent, recv: tcp.recv, loss: 0, last: r3(tcp.last), best: r3(tcp.best), avg: r3(tcp.mean), worst: r3(tcp.worst), status: 'open' } : null,
        });
        emit('route.geo', geo);
        const r = runs.find((x) => x.id === current.runId);
        if (r) {
          r.avg = vpn ? vpn.avg : dest.avg;
          r.loss = vpn ? vpn.loss : dest.loss;
          r.best = vpn ? vpn.best : dest.best;
          r.jitter = dest.jAvg;
          r.snapshots = Math.ceil(round / 60);
        }
      };
      tick();
      timer = window.setInterval(tick, Math.max(100, opts.intervalMs));
    }, isIp ? 120 : 420);
    return id;
  };

  const connections = (): Connection[] => {
    const now = Date.now();
    const list: Connection[] = [];
    const remotes = ['149.154.167.51', '142.250.185.78', '104.16.132.229', '162.159.130.234', '23.62.99.10', '20.42.65.92', '13.107.42.16', '185.199.108.153'];
    for (let i = 0; i < 26; i++) {
      const [proc, path] = processes[i % processes.length];
      const remote = remotes[(i * 3) % remotes.length];
      const listen = i % 9 === 0;
      list.push({
        key: `tcp|${i}`, proto: i % 7 === 3 ? 'udp' : 'tcp', family: 4, localAddr: i % 2 ? '10.0.0.2' : '10.8.0.2', localPort: 50000 + i * 37,
        remoteAddr: listen ? null : remote, remotePort: listen ? null : [443, 443, 80, 27015, 5222][i % 5],
        state: listen ? 'LISTEN' : i % 7 === 3 ? 'BOUND' : i % 11 === 5 ? 'TIME_WAIT' : 'ESTABLISHED', pid: 1000 + (i % processes.length) * 412, process: proc, path,
        adapterId: i % 2 ? '{B2}' : '{A1}', adapterName: i % 2 ? 'Ethernet' : 'tun0', remoteKind: listen ? 'none' : 'public',
        remoteHost: listen ? null : ['api.telegram.org', 'fra16s56-in-f14.1e100.net', null, null, 'a23-62-99-10.deploy.akamaitechnologies.com'][i % 5],
        remoteCountry: listen ? null : ['NL', 'DE', 'US', 'US', 'SE'][i % 5], remoteCity: listen ? null : [L('Амстердам', 'Amsterdam'), L('Франкфурт', 'Frankfurt'), null, L('Сан-Франциско', 'San Francisco'), L('Стокгольм', 'Stockholm')][i % 5],
        remoteAsName: listen ? null : ['Telegram Messenger', 'Google LLC', 'Cloudflare', 'Cloudflare', 'Akamai'][i % 5], firstSeen: now - i * 61_000,
      });
    }
    return list;
  };

  const handle = async (type: string, p: Record<string, unknown> | undefined): Promise<unknown> => {
    switch (type) {
      case 'app.info':
        return { version: '1.1.1', language: settings.language ?? undefined, repo: settings.updateRepo, isAdmin: false, dataDir: 'C:\\Users\\user\\AppData\\Local\\MTRTracer', sessionsDir: '…\\sessions', machine: 'DESKTOP', os: 'Windows 11', history: true };
      case 'settings.get':
        return settings;
      case 'settings.set':
        if (p?.options) settings.options = p.options as TraceOptions;
        if (p && 'ui' in p) settings.ui = p.ui;
        if (p?.origin) {
          settings.origin = p.origin as AppSettings['origin'];
          emit('geo.origin', originInfo());
        }
        if (p && 'vpnMode' in p) settings.vpnMode = !!p.vpnMode;
        if (p && 'vpnServer' in p) settings.vpnServer = (p.vpnServer as string) || null;
        if (p && 'language' in p) settings.language = (p.language as AppSettings['language']) ?? null;
        if (p?.providers) {
          settings.providers = p.providers as AppSettings['providers'];
          geo.enabled = settings.providers.offlineDb;
          emit('geo.status', { ...geo });
        }
        if (p?.geoDb) settings.geoDb = { ...settings.geoDb, ...(p.geoDb as Partial<AppSettings['geoDb']>) };
        if (p && 'updateRepo' in p) settings.updateRepo = /^[\w.-]+\/[\w.-]+$/.test(String(p.updateRepo ?? '')) ? String(p.updateRepo) : 'extraut/MTRTracer';
        save();
        return settings;
      case 'adapters.list':
        await new Promise((r) => setTimeout(r, 250));
        return { adapters, selectedId: settings.adapterId, chosen: settings.adapterChosen, vpnMode: settings.vpnMode, tunnels: [tunnel] };
      case 'adapters.select':
        settings.adapterId = (p?.adapterId as string | null) ?? null;
        settings.adapterChosen = true;
        if (p && 'vpnMode' in p) settings.vpnMode = !!p.vpnMode;
        save();
        return { adapterId: settings.adapterId, vpnMode: settings.vpnMode };
      case 'tunnel.detect':
        return tunnel;
      case 'adapters.watch': {
        clearInterval(trafficTimer);
        if (p?.on) {
          const counters = new Map(adapters.map((a) => [a.id, { s: a.bytesSent, r: a.bytesRecv }]));
          trafficTimer = window.setInterval(() => {
            const items = adapters.filter((a) => a.status === 'up').map((a) => {
              const c = counters.get(a.id)!;
              const k = a.kind === 'virtual' ? 0.01 : a.kind === 'vpn' ? 0.6 : 1.3;
              c.s += Math.round(Math.random() * 90_000 * k);
              c.r += Math.round(Math.random() * 900_000 * k);
              return { id: a.id, sent: c.s, recv: c.r };
            });
            emit('adapters.traffic', { t: Date.now(), items });
          }, 1000);
        }
        return null;
      }
      case 'trace.start': {
        const opts = (p?.options as TraceOptions) ?? settings.options;
        settings.options = opts;
        if (p && 'vpnMode' in p) settings.vpnMode = !!p.vpnMode;
        const target = String(p?.target ?? '');
        settings.recent = [target, ...settings.recent.filter((r) => r !== target)].slice(0, 12);
        save();
        return { sessionId: startSession(target, opts) };
      }
      case 'trace.stop':
        if (session && session.state.state !== 'error') {
          session.stop();
          session.state.state = 'stopped';
          const r = runs.find((x) => x.id === session!.runId);
          if (r) r.endedAt = Date.now();
          emit('trace.state', { ...session.state });
          emit('trace.event', { t: Date.now(), level: 'info', code: 'stopped', message: L('Остановлено.', 'Stopped.'), ttl: null, ip: null });
          emit('history.changed', { target: session.state.overlayInput ?? session.state.host });
        }
        return null;
      case 'trace.pause':
        if (session) {
          session.paused = !!p?.paused;
          session.state.state = session.paused ? 'paused' : 'running';
          emit('trace.state', { ...session.state });
        }
        return null;
      case 'trace.reset':
        if (!session) throw { code: 'no-session', message: L('Нет активной трассировки.', 'No active trace.') };
        return { sessionId: startSession(session.input, settings.options) };
      case 'trace.pmtu':
        await new Promise((r) => setTimeout(r, 1400));
        return { mtu: 1420, ms: 1400 };
      case 'trace.dnsBench':
        await new Promise((r) => setTimeout(r, 500));
        return { host: 'google.com', results: [
          { resolver: '10.0.0.1', via: 'adapter-dns', ms: 3.1, ok: true, error: null, addresses: ['142.250.185.78'] },
          { resolver: '1.1.1.1', via: 'public-dns', ms: 14.7, ok: true, error: null, addresses: ['142.250.185.78'] },
          { resolver: '9.9.9.9', via: 'public-dns', ms: 2500, ok: false, error: L('таймаут', 'timeout'), addresses: [] },
        ] };
      case 'geo.status':
        return { ...geo, online: settings.options.geoOnline };
      case 'geo.download':
        simulateDownload(String(p?.month ?? '2026-10'));
        return null;
      case 'geo.db.list':
        return { releases: releases(), active: geo.active, pinned: settings.geoDb.active, setting: settings.geoDb, dir: '…\\geo' };
      case 'geo.db.available':
        await new Promise((r) => setTimeout(r, 400));
        return ['2026-10', '2026-09', '2026-08', '2026-07', '2026-06', '2026-05'].map((m, i) => ({ month: m, available: i < 2, bytes: i < 2 ? 62_000_000 : null, installed: installed.has(m) }));
      case 'geo.db.use':
        settings.geoDb.active = (p?.id as string | null) ?? null;
        geo.active = settings.geoDb.active ?? [...installed].sort().at(-1) ?? null;
        geo.dbDate = geo.active === 'custom' ? 'GeoLite2-City' : geo.active;
        save();
        emit('geo.status', { ...geo });
        return { active: geo.active };
      case 'geo.db.delete':
        installed.delete(String(p?.id));
        if (settings.geoDb.active === p?.id) settings.geoDb.active = null;
        geo.active = settings.geoDb.active ?? [...installed].filter((m) => m !== 'custom').sort().at(-1) ?? null;
        geo.dbDate = geo.active;
        geo.db = geo.active ? 'ready' : 'missing';
        emit('geo.status', { ...geo });
        return { active: geo.active };
      case 'geo.db.import':
      case 'geo.db.fetch':
        await new Promise((r) => setTimeout(r, 600));
        installed.add('custom');
        settings.geoDb.active = 'custom';
        geo.active = 'custom';
        geo.dbDate = 'GeoLite2-City';
        save();
        emit('geo.status', { ...geo });
        return { kind: 'city', active: 'custom' };
      case 'geo.cache.stats':
        return { ...cache };
      case 'geo.cache.clear':
        Object.assign(cache, { ripeIpmap: 0, ripeStatGeo: 0, ripeStatNet: 0, ipApi: 0 });
        return { ...cache };
      case 'app.checkUpdate': {
        await new Promise((r) => setTimeout(r, 500));
        const fork = settings.updateRepo !== 'extraut/MTRTracer';
        const tag = fork ? 'v1.2.0' : 'v1.1.1';
        return {
          repo: settings.updateRepo, current: '1.1.1', latest: tag, newerAvailable: fork, url: `https://github.com/${settings.updateRepo}/releases/tag/${tag}`,
          publishedAt: new Date().toISOString(), notes: fork ? L('Демо: так выглядит найденное обновление.', 'Demo: this is what a found update looks like.') : null,
          assets: [
            { name: 'MTRTracer-windows-x64.exe', url: `https://github.com/${settings.updateRepo}/releases/download/${tag}/MTRTracer-windows-x64.exe`, size: 64_500_000 },
            { name: 'mtrtracer-linux-x64.tar.gz', url: `https://github.com/${settings.updateRepo}/releases/download/${tag}/mtrtracer-linux-x64.tar.gz`, size: 34_100_000 },
          ],
          archived: false, error: null,
        };
      }
      case 'shell.url':
        window.open(String(p?.url), '_blank', 'noopener');
        return null;
      case 'file.pick':
        throw { code: 'unsupported', message: L('В демо-режиме нет диалога выбора файла. Введите путь вручную.', 'No file dialog in demo mode. Enter the path manually.') };
      case 'shell.open':
        return null;
      case 'geo.self':
        return originInfo();
      case 'geo.cities':
        return cities;
      case 'host.get':
        return hostInfos.get(String(p?.ip)) ?? null;
      case 'history.targets': {
        const keys = [...new Set(runs.map((r) => r.targetKey))];
        return keys
          .map((k) => {
            const rs = runs.filter((r) => r.targetKey === k).sort((a, b) => a.startedAt - b.startedAt);
            const last = rs[rs.length - 1];
            return { targetKey: k, runs: rs.length, lastAt: last.endedAt ?? last.startedAt, lastAvg: last.avg, lastLoss: last.loss, avgTrend: rs.map((r) => r.avg) };
          })
          .sort((a, b) => b.lastAt - a.lastAt);
      }
      case 'history.runs':
        return runs.filter((r) => r.targetKey === p?.target).sort((a, b) => b.startedAt - a.startedAt);
      case 'history.run': {
        const r = runs.find((x) => x.id === p?.id);
        if (!r) return null;
        const sim = route(r.targetKey, r.targetIp ?? '1.1.1.1');
        const k = r.avg && sim.at(-1)!.base ? r.avg / sim.at(-1)!.base : 1;
        const hops = sim.map((h, i) => ({
          ttl: i + 1, ip: h.silent ? null : h.ips[r.routeHash === 'f0e1d2c3b4a5' && i === 6 ? h.ips.length - 1 : 0], host: h.host[0], loss: h.silent ? 100 : Math.round(h.loss * 100 * (r.loss ? 2 : 1) * 10) / 10,
          sent: 600, avg: h.silent ? null : Math.round(h.base * k * 10) / 10, best: h.silent ? null : Math.round(h.base * k * 9) / 10, worst: null, jAvg: 1.2,
        }));
        return { run: r, snapshots: [], hops };
      }
      case 'history.delete':
        if (p?.id) runs.splice(runs.findIndex((r) => r.id === p.id), 1);
        else if (p?.target) for (let i = runs.length - 1; i >= 0; i--) if (runs[i].targetKey === p.target) runs.splice(i, 1);
        return null;
      case 'connections.subscribe':
        clearInterval(connTimer);
        if (p?.on) {
          const push = () => emit('connections.snapshot', { t: Date.now(), items: connections() });
          push();
          connTimer = window.setInterval(push, 1000);
        }
        return null;
      case 'export.save':
        return p?.format === 'clipboard' ? { copied: true } : { path: `C:\\Users\\user\\Documents\\mtr-export.${p?.format}` };
      case 'clipboard.copy':
        await navigator.clipboard?.writeText(String(p?.text ?? '')).catch(() => undefined);
        return null;
      case 'process.icon':
        return { dataUrl: null };
      case 'shell.reveal':
        return null;
      case 'window.state':
      case 'window.toggleMaximize':
        return { maximized: false };
      default:
        return null;
    }
  };

  setTimeout(() => {
    emit('geo.status', { ...geo });
    emit('geo.origin', originInfo());
  }, 50);

  return {
    post(message: unknown) {
      const m = message as { id: number; type: string; payload?: Record<string, unknown> };
      handle(m.type, m.payload).then(
        (result) => setTimeout(() => handlers.forEach((h) => h({ kind: 'res', id: m.id, ok: true, result })), 10),
        (error: { code?: string; message?: string }) =>
          setTimeout(() => handlers.forEach((h) => h({ kind: 'res', id: m.id, ok: false, error: { code: error.code ?? 'error', message: error.message ?? String(error) } })), 10),
      );
    },
    subscribe(h: Handler) {
      handlers.push(h);
    },
  };
}
