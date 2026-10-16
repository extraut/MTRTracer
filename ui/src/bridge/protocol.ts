// Shared message contract with the C# host (src/MTRTracer.Core/Protocol/Dtos.cs, AppController.cs).

export type AdapterKind = 'ethernet' | 'wifi' | 'vpn' | 'virtual' | 'loopback' | 'cellular' | 'other';

export interface IpAddrInfo {
  address: string;
  prefix: number;
}

export interface AdapterInfo {
  id: string;
  name: string;
  description: string;
  kind: AdapterKind;
  status: 'up' | 'down' | 'dormant' | 'not-present' | 'other';
  mac: string | null;
  speedBps: number;
  mtu: number | null;
  ifIndex: number;
  ipv6IfIndex: number;
  ifType: string;
  ipv4: IpAddrInfo[];
  ipv6: IpAddrInfo[];
  gateways: string[];
  dns: string[];
  dnsSuffix: string | null;
  dhcp: boolean;
  hasGateway: boolean;
  hasRoute: boolean;
  hasRouteV6: boolean;
  isWindowsChoice: boolean;
  isWindowsChoiceV6: boolean;
  routeMetric: number | null;
  bytesSent: number;
  bytesRecv: number;
}

export interface TraceOptions {
  intervalMs: number;
  timeoutMs: number;
  packetSize: number;
  maxHops: number;
  burst: number;
  dontFragment: boolean;
  ipVersion: 'auto' | '4' | '6';
  maxRounds: number;
  resolveNames: boolean;
  tcpFallback: boolean;
  tcpPort: number;
  geoOnline: boolean;
  payload: string;
}

export const defaultOptions: TraceOptions = {
  intervalMs: 1000,
  timeoutMs: 2000,
  packetSize: 64,
  maxHops: 30,
  burst: 1,
  dontFragment: false,
  ipVersion: 'auto',
  maxRounds: 0,
  resolveNames: true,
  tcpFallback: true,
  tcpPort: 443,
  geoOnline: true,
  payload: 'MTRTracer',
};

export interface OriginSetting {
  mode: 'auto' | 'manual';
  lat: number | null;
  lon: number | null;
  label: string | null;
}

export interface AppSettings {
  adapterId: string | null;
  adapterChosen: boolean;
  vpnMode: boolean;
  vpnServer: string | null;
  origin: OriginSetting;
  options: TraceOptions;
  recent: string[];
  ui: unknown;
  language: Lang | null;
  providers: GeoProviders;
  geoDb: GeoDbSetting;
  updateRepo: string;
}

export type Lang = 'ru' | 'en';

export interface GeoProviders {
  offlineDb: boolean;
  hostnameHints: boolean;
  ripeIpmap: boolean;
  ripeStat: boolean;
  ipApi: boolean;
  cloudflareSelf: boolean;
  cloudflareColo: boolean;
  teamCymru: boolean;
}

export const defaultProviders: GeoProviders = {
  offlineDb: true,
  hostnameHints: true,
  ripeIpmap: true,
  ripeStat: true,
  ipApi: true,
  cloudflareSelf: true,
  cloudflareColo: true,
  teamCymru: true,
};

export interface GeoDbSetting {
  autoUpdate: boolean;
  active: string | null;
  urlTemplate: string;
}

export const defaultUrlTemplate = 'https://download.db-ip.com/free/{name}-{month}.mmdb.gz';

export interface GeoDbRelease {
  id: string;
  label: string;
  city: boolean;
  asn: boolean;
  bytes: number;
  type: string | null;
  built: string | null;
  active: boolean;
}

export interface GeoDbAvailability {
  month: string;
  available: boolean;
  bytes: number | null;
  installed: boolean;
}

export interface CacheStats {
  hosts: number;
  ripeIpmap: number;
  ripeStatGeo: number;
  ripeStatNet: number;
  ipApi: number;
}

export interface UpdateInfo {
  repo: string;
  current: string;
  latest: string | null;
  newerAvailable: boolean;
  url: string | null;
  publishedAt: string | null;
  notes: string | null;
  assets: { name: string; url: string; size: number }[];
  archived: boolean;
  error: string | null;
}

export type TraceStateName = 'resolving' | 'running' | 'paused' | 'waiting-network' | 'stopped' | 'finished' | 'error';

export interface TraceState {
  sessionId: string;
  state: TraceStateName;
  input: string;
  host: string | null;
  targetIp: string | null;
  sourceIp: string | null;
  adapterId: string | null;
  family: '4' | '6' | null;
  startedAt: number;
  error: string | null;
  errorCode: string | null;
  outageSince: number | null;
  port: number | null;
  mode: 'normal' | 'vpn';
  overlayInput: string | null;
  overlayIp: string | null;
  carrierName: string | null;
}

export interface ResolveAttempt {
  resolver: string;
  via: string;
  ms: number | null;
  ok: boolean;
  error: string | null;
  addresses: string[];
}

export interface Resolved {
  sessionId: string;
  input: string;
  host: string;
  address: string | null;
  addresses: string[];
  via: 'literal' | 'system' | 'adapter-dns' | 'public-dns' | 'none';
  ms: number | null;
  attempts: ResolveAttempt[];
}

export interface Responder {
  ip: string;
  count: number;
  share: number;
  avg: number | null;
  best: number | null;
  last: number | null;
}

export interface Hop {
  ttl: number;
  primary: string | null;
  responders: Responder[];
  sent: number;
  recv: number;
  loss: number;
  last: number | null;
  best: number | null;
  avg: number | null;
  worst: number | null;
  stDev: number | null;
  gMean: number | null;
  jitter: number | null;
  jAvg: number | null;
  jMax: number | null;
  jInt: number | null;
  status: string;
  history: (number | null)[];
  isDestination: boolean;
  consecutiveLoss: number;
}

export interface TcpStats {
  port: number;
  sent: number;
  recv: number;
  loss: number;
  last: number | null;
  best: number | null;
  avg: number | null;
  worst: number | null;
  status: string;
}

export interface Note {
  level: 'ok' | 'info' | 'warn' | 'error';
  code: string;
  text: string;
  ttl: number | null;
}

export interface HopsSnapshot {
  sessionId: string;
  round: number;
  destTtl: number | null;
  hops: Hop[];
  tcp: TcpStats | null;
  notes: Note[];
  elapsedMs: number;
  vpn: VpnBlock | null;
}

export interface VpnExit {
  ip: string | null;
  asOrg: string | null;
  asn: number | null;
  city: string | null;
  country: string | null;
  lat: number | null;
  lon: number | null;
  colo: string | null;
  coloCity: string | null;
  coloLat: number | null;
  coloLon: number | null;
  fetchedAt: number;
}

export interface VpnBlock {
  server: string;
  serverPort: number;
  process: string | null;
  tunnel: string | null;
  carrier: string | null;
  exit: VpnExit | null;
  target: string;
  targetIp: string | null;
  port: number;
  method: 'https' | 'http';
  sent: number;
  recv: number;
  loss: number;
  last: number | null;
  best: number | null;
  avg: number | null;
  worst: number | null;
  jAvg: number | null;
  setupMs: number | null;
  connectFaked: boolean;
  serverBest: number | null;
  legMs: number | null;
  status: string;
  history: (number | null)[];
  targetGeo: HopGeo | null;
}

export interface TunnelServerCandidate {
  ip: string;
  port: number;
  connections: number;
}

export interface TunnelInfo {
  adapterId: string;
  adapterName: string;
  isDefaultRoute: boolean;
  process: string | null;
  pid: number | null;
  processPath: string | null;
  servers: TunnelServerCandidate[];
  carrierId: string | null;
  carrierName: string | null;
  carrierIp: string | null;
  explanation: string;
}

export type GeoStatusName = 'confirmed' | 'single' | 'anycast' | 'unresolved' | 'pending' | 'local' | 'silent';

export interface CandidateVerdict {
  source: string;
  lat: number;
  lon: number;
  city: string | null;
  countryCode: string | null;
  level: string;
  fetchedAt: number;
  accepted: boolean;
  reason: string | null;
}

export interface HopGeo {
  ttl: number;
  ip: string | null;
  lat: number | null;
  lon: number | null;
  status: GeoStatusName;
  source: string | null;
  city: string | null;
  countryCode: string | null;
  reason: string | null;
  verdicts: CandidateVerdict[];
  maxDistanceKm: number | null;
}

export interface GeoPoint {
  lat: number;
  lon: number;
  label: string | null;
  source: string;
}

export interface RouteGeo {
  sessionId: string;
  origin: GeoPoint | null;
  hops: HopGeo[];
  summary: { pathKm: number; directKm: number | null; minRttMs: number | null; measuredMs: number | null; stretch: number | null; unresolved: number };
  originNote?: string | null;
}

export interface OriginInfo {
  mode: 'auto' | 'manual';
  manual: OriginSetting | null;
  publicIp: string | null;
  asn: number | null;
  asOrg: string | null;
  city: string | null;
  region: string | null;
  country: string | null;
  lat: number | null;
  lon: number | null;
  colo: string | null;
  coloCity: string | null;
  via: string | null;
  source: string | null;
  fetchedAt: number | null;
  note?: string | null;
  point: GeoPoint | null;
}

export interface HistoryHop {
  ttl: number;
  ip: string | null;
  host: string | null;
  loss: number;
  sent: number;
  avg: number | null;
  best: number | null;
  worst: number | null;
  jAvg: number | null;
}

export interface RunSummary {
  id: number;
  targetKey: string;
  input: string;
  targetIp: string | null;
  mode: string;
  adapterName: string | null;
  sourceIp: string | null;
  publicIp: string | null;
  startedAt: number;
  endedAt: number | null;
  snapshots: number;
  loss: number | null;
  avg: number | null;
  best: number | null;
  jitter: number | null;
  hops: number;
  routeHash: string | null;
}

export interface TargetSummary {
  targetKey: string;
  runs: number;
  lastAt: number;
  lastAvg: number | null;
  lastLoss: number | null;
  avgTrend: (number | null)[];
}

export interface RunDetail {
  run: RunSummary;
  snapshots: { t: number; loss: number | null; avg: number | null; best: number | null; worst: number | null; jitter: number | null; routeHash: string; rounds: number }[];
  hops: HistoryHop[];
}

export interface ProbeRow {
  t: number;
  round: number;
  seq: number;
  ttl: number;
  src: string | null;
  dst: string;
  from: string | null;
  status: string;
  rawStatus: number;
  rttMs: number | null;
  kernelMs: number;
  size: number;
  proto: string;
}

export interface TraceEvent {
  t: number;
  level: 'info' | 'warn' | 'error';
  code: string;
  message: string;
  ttl: number | null;
  ip: string | null;
}

export interface GeoCandidate {
  source: string;
  lat: number;
  lon: number;
  city: string | null;
  region: string | null;
  countryCode: string | null;
  level: string;
  fetchedAt: number;
  note: string | null;
}

export interface Geo {
  lat: number;
  lon: number;
  city: string | null;
  region: string | null;
  country: string | null;
  countryCode: string | null;
  source: 'dbip' | 'ipapi' | 'hint' | 'local' | 'self';
  accuracy: 'city' | 'country' | 'approx';
}

export interface HostInfo {
  ip: string;
  kind: 'public' | 'private' | 'cgnat' | 'link-local' | 'loopback' | 'multicast' | 'ula' | 'reserved';
  hostname: string | null;
  hostnameDone: boolean;
  asn: number | null;
  asName: string | null;
  asSource: string | null;
  prefix: string | null;
  registry: string | null;
  isp: string | null;
  org: string | null;
  anycast: boolean;
  hintToken: string | null;
  candidates: GeoCandidate[];
  pending: number;
}

export interface GeoStatus {
  db: 'missing' | 'downloading' | 'updating' | 'ready' | 'error';
  progress: number;
  stage: string | null;
  dbDate: string | null;
  error: string | null;
  online: boolean;
  onlineError: string | null;
  active?: string | null;
  enabled?: boolean;
  autoUpdate?: boolean;
}

export interface SelfGeo {
  publicIp: string | null;
  isp: string | null;
  asName: string | null;
  geo: Geo | null;
}

export interface Connection {
  key: string;
  proto: 'tcp' | 'udp';
  family: 4 | 6;
  localAddr: string;
  localPort: number;
  remoteAddr: string | null;
  remotePort: number | null;
  state: string;
  pid: number;
  process: string;
  path: string | null;
  adapterId: string | null;
  adapterName: string | null;
  remoteKind: string;
  remoteHost: string | null;
  remoteCountry: string | null;
  remoteCity: string | null;
  remoteAsName: string | null;
  firstSeen: number;
}

export interface ConnBrief {
  key: string;
  proto: string;
  process: string;
  pid: number;
  remoteAddr: string | null;
  remotePort: number | null;
  remoteHost: string | null;
  localPort: number;
}

export interface AppInfo {
  version: string;
  language?: Lang;
  systemLanguage?: Lang;
  repo?: string;
  platform?: string;
  isAdmin: boolean;
  dataDir: string;
  sessionsDir: string;
  machine: string;
  os: string;
}

export type ExportFormat = 'txt' | 'csv' | 'json' | 'html' | 'clipboard';

/** Events pushed by the host. */
export interface EventMap {
  'trace.state': TraceState;
  'trace.resolved': Resolved;
  'trace.hops': HopsSnapshot;
  'trace.probes': { rows: ProbeRow[] };
  'trace.event': TraceEvent;
  'host.info': HostInfo;
  'geo.status': GeoStatus;
  'adapters.traffic': { t: number; items: { id: string; sent: number; recv: number }[] };
  'connections.snapshot': { t: number; items: Connection[] };
  'connections.events': { t: number; opened: ConnBrief[]; closed: ConnBrief[] };
  'window.state': { maximized: boolean };
  'route.geo': RouteGeo;
  'geo.origin': OriginInfo;
  'history.previous': { sessionId: string; run: RunSummary };
  'history.changed': { target: string };
}

/** Requests the page can make, with their results. */
export interface RequestMap {
  'app.info': [undefined, AppInfo];
  'settings.get': [undefined, AppSettings];
  'settings.set': [
    {
      options?: TraceOptions;
      ui?: unknown;
      origin?: OriginSetting;
      vpnMode?: boolean;
      vpnServer?: string | null;
      language?: Lang | null;
      providers?: GeoProviders;
      geoDb?: Partial<GeoDbSetting>;
      updateRepo?: string;
    },
    AppSettings,
  ];
  'adapters.list': [undefined, { adapters: AdapterInfo[]; selectedId: string | null; chosen: boolean; vpnMode: boolean; tunnels: TunnelInfo[] }];
  'adapters.select': [{ adapterId: string | null; vpnMode?: boolean }, { adapterId: string | null; vpnMode: boolean }];
  'tunnel.detect': [{ adapterId?: string } | undefined, TunnelInfo | null];
  'adapters.watch': [{ on: boolean }, null];
  'trace.start': [{ target: string; options?: TraceOptions; vpnMode?: boolean }, { sessionId: string }];
  'trace.stop': [undefined, null];
  'trace.pause': [{ paused: boolean }, null];
  'trace.reset': [undefined, { sessionId: string }];
  'trace.pmtu': [undefined, { mtu: number | null; ms: number }];
  'trace.dnsBench': [{ host?: string } | undefined, { host: string; results: ResolveAttempt[] }];
  'geo.status': [undefined, GeoStatus];
  'geo.download': [{ month?: string } | undefined, null];
  'geo.db.list': [undefined, { releases: GeoDbRelease[]; active: string | null; pinned: string | null; setting: GeoDbSetting; dir: string }];
  'geo.db.available': [{ count?: number }, GeoDbAvailability[]];
  'geo.db.use': [{ id: string | null }, { active: string | null }];
  'geo.db.delete': [{ id: string }, { active: string | null }];
  'geo.db.import': [{ path: string }, { kind: 'city' | 'asn'; active: string | null }];
  'geo.db.fetch': [{ url: string }, { kind: 'city' | 'asn'; active: string | null }];
  'geo.cache.stats': [undefined, CacheStats];
  'geo.cache.clear': [undefined, CacheStats];
  'app.checkUpdate': [undefined, UpdateInfo];
  'shell.url': [{ url: string }, null];
  'file.pick': [undefined, { path: string | null }];
  'geo.self': [{ force?: boolean } | undefined, OriginInfo];
  'geo.cities': [undefined, { city: string; cc: string; lat: number; lon: number }[]];
  'history.targets': [undefined, TargetSummary[]];
  'history.runs': [{ target: string }, RunSummary[]];
  'history.run': [{ id: number }, RunDetail | null];
  'history.delete': [{ id?: number; target?: string }, null];
  'shell.reveal': [{ path: string }, null];
  'host.get': [{ ip: string }, HostInfo | null];
  'connections.subscribe': [{ on: boolean }, null];
  'export.save': [{ format: ExportFormat }, { path?: string; cancelled?: boolean; copied?: boolean }];
  'clipboard.copy': [{ text: string }, null];
  'shell.open': [{ path: 'sessions' | 'data' }, null];
  'process.icon': [{ path: string }, { dataUrl: string | null }];
  'window.minimize': [undefined, null];
  'window.toggleMaximize': [undefined, { maximized: boolean }];
  'window.close': [undefined, null];
  'window.drag': [undefined, null];
  'window.state': [undefined, { maximized: boolean }];
}
