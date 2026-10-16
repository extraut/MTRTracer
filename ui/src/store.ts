import { create } from 'zustand';
import { on, request } from './bridge/client';
import { lang as i18nLang, setLang as setI18nLang } from './i18n';
import { applyTheme, resolveTheme, savedTheme, type ResolvedTheme, type ThemeId } from './theme';
import {
  defaultOptions,
  defaultProviders,
  defaultUrlTemplate,
  type GeoDbSetting,
  type GeoProviders,
  type UpdateInfo,
  type AdapterInfo,
  type AppInfo,
  type Connection,
  type ConnBrief,
  type GeoStatus,
  type Hop,
  type HostInfo,
  type Note,
  type ProbeRow,
  type OriginInfo,
  type OriginSetting,
  type Resolved,
  type RouteGeo,
  type RunSummary,
  type TunnelInfo,
  type TcpStats,
  type TraceEvent,
  type TraceOptions,
  type TraceState,
  type VpnBlock,
} from './bridge/protocol';

export type DockTab = 'probes' | 'events' | 'chart' | 'connections' | 'details';
export type Projection = 'globe' | 'mercator';
export type View = 'trace' | 'history';
export type Lang = 'ru' | 'en';
export type SettingsTab = 'general' | 'geo' | 'sources' | 'updates';

export const allColumns = ['host', 'asn', 'loss', 'sent', 'recv', 'last', 'avg', 'best', 'worst', 'stdev', 'gmean', 'jitter', 'javg', 'jmax', 'jint', 'history'] as const;
export type Column = (typeof allColumns)[number];
export const defaultColumns: Column[] = ['host', 'asn', 'loss', 'sent', 'recv', 'last', 'avg', 'best', 'worst', 'stdev', 'javg', 'history'];

export interface UiPrefs {
  lang: Lang;
  theme: ThemeId;
  projection: Projection;
  mapDetail: boolean;
  columns: Column[];
  dockTab: DockTab;
  dockHeight: number;
  mapWidth: number;
  logAutoScroll: boolean;
}

const defaultUi: UiPrefs = {
  lang: 'ru',
  theme: savedTheme(),
  projection: 'mercator',
  mapDetail: false,
  columns: defaultColumns,
  dockTab: 'probes',
  dockHeight: 280,
  mapWidth: 0.42,
  logAutoScroll: true,
};

// ---------------- high-rate buffers kept outside React state ----------------

export const PROBE_CAP = 100_000;
export const probeBuffer: ProbeRow[] = [];
/** Per-TTL latency series for the chart: [unix seconds[], rtt ms (NaN = lost)[]]. */
export const series = new Map<number, { t: number[]; v: number[] }>();
export const SERIES_CAP = 7200;

function pushProbes(rows: ProbeRow[]) {
  for (const r of rows) {
    probeBuffer.push(r);
    if (r.proto !== 'icmp') continue;
    let s = series.get(r.ttl);
    if (!s) series.set(r.ttl, (s = { t: [], v: [] }));
    s.t.push(r.t / 1000);
    s.v.push(r.rttMs ?? NaN);
    if (s.t.length > SERIES_CAP) {
      s.t.splice(0, s.t.length - SERIES_CAP);
      s.v.splice(0, s.v.length - SERIES_CAP);
    }
  }
  if (probeBuffer.length > PROBE_CAP) probeBuffer.splice(0, probeBuffer.length - PROBE_CAP);
}

// ---------------- store ----------------

export interface Store {
  ready: boolean;
  info: AppInfo | null;
  adapters: AdapterInfo[];
  adaptersLoading: boolean;
  selectedAdapterId: string | null;
  adapterGateOpen: boolean;
  traffic: Record<string, { t: number; sent: number; recv: number; upBps: number; downBps: number }>;

  options: TraceOptions;
  recent: string[];
  ui: UiPrefs;

  session: TraceState | null;
  resolved: Resolved | null;
  hops: Hop[];
  destTtl: number | null;
  round: number;
  elapsedMs: number;
  tcp: TcpStats | null;
  vpn: VpnBlock | null;
  notes: Note[];
  events: TraceEvent[];
  probeVersion: number;
  hosts: Record<string, HostInfo>;
  geoStatus: GeoStatus | null;
  origin: OriginInfo | null;
  originSetting: OriginSetting;
  routeGeo: RouteGeo | null;
  tunnels: TunnelInfo[];
  vpnMode: boolean;
  vpnServer: string | null;
  view: View;
  previousRun: RunSummary | null;

  lang: Lang;
  theme: ThemeId;
  resolvedTheme: ResolvedTheme;
  settingsTab: SettingsTab | null;
  providers: GeoProviders;
  geoDb: GeoDbSetting;
  updateRepo: string;
  update: UpdateInfo | null;
  updateChecking: boolean;
  updateAvailable: boolean;

  connections: Connection[];
  connLog: { t: number; kind: 'open' | 'close'; c: ConnBrief }[];

  hoverTtl: number | null;
  selectedTtl: number | null;
  maximized: boolean;
  toast: { id: number; text: string; level: 'info' | 'error' } | null;

  init(): Promise<void>;
  loadAdapters(): Promise<void>;
  openAdapterGate(open: boolean): void;
  chooseAdapter(id: string | null, vpnMode?: boolean): Promise<void>;
  setView(v: View): void;
  setOrigin(o: OriginSetting): Promise<void>;
  setVpnServer(v: string | null): Promise<void>;
  setVpnMode(v: boolean): Promise<void>;
  start(target: string): Promise<void>;
  stop(): Promise<void>;
  pause(paused: boolean): Promise<void>;
  reset(): Promise<void>;
  setOptions(patch: Partial<TraceOptions>): void;
  setUi(patch: Partial<UiPrefs>): void;
  setHover(ttl: number | null): void;
  setSelected(ttl: number | null): void;
  notify(text: string, level?: 'info' | 'error'): void;

  setLang(l: Lang): Promise<void>;
  setTheme(t: ThemeId): void;
  openSettings(tab: SettingsTab | null): void;
  setProviders(p: Partial<GeoProviders>): Promise<void>;
  setGeoDb(p: Partial<GeoDbSetting>): Promise<void>;
  setUpdateRepo(repo: string): Promise<void>;
  checkUpdate(): Promise<void>;
}

let saveTimer: number | undefined;
let initStarted = false;

/** Re-applies "system" when the OS switches between light and dark. */
function watchSystemTheme(get: () => Store, set: (p: Partial<Store>) => void) {
  try {
    window.matchMedia('(prefers-color-scheme: light)').addEventListener('change', () => {
      if (get().theme !== 'system') return;
      applyTheme('system');
      set({ resolvedTheme: resolveTheme('system') });
    });
  } catch {
    /* no matchMedia */
  }
}
let toastId = 0;

export const useStore = create<Store>((set, get) => ({
  ready: false,
  info: null,
  adapters: [],
  adaptersLoading: false,
  selectedAdapterId: null,
  adapterGateOpen: true,
  traffic: {},

  options: { ...defaultOptions },
  recent: [],
  ui: { ...defaultUi },

  session: null,
  resolved: null,
  hops: [],
  destTtl: null,
  round: 0,
  elapsedMs: 0,
  tcp: null,
  vpn: null,
  notes: [],
  events: [],
  probeVersion: 0,
  hosts: {},
  geoStatus: null,
  origin: null,
  originSetting: { mode: 'auto', lat: null, lon: null, label: null },
  routeGeo: null,
  tunnels: [],
  vpnMode: false,
  vpnServer: null,
  view: 'trace',
  previousRun: null,

  lang: i18nLang(),
  theme: savedTheme(),
  resolvedTheme: resolveTheme(savedTheme()),
  settingsTab: null,
  providers: { ...defaultProviders },
  geoDb: { autoUpdate: true, active: null, urlTemplate: defaultUrlTemplate },
  updateRepo: 'extraut/MTRTracer',
  update: null,
  updateChecking: false,
  updateAvailable: false,

  connections: [],
  connLog: [],

  hoverTtl: null,
  selectedTtl: null,
  maximized: false,
  toast: null,

  async init() {
    if (initStarted) return;
    initStarted = true;
    subscribe(set, get);
    const [settings, info, geo] = await Promise.all([request('settings.get'), request('app.info'), request('geo.status')]);
    const ui = { ...defaultUi, ...((settings.ui as Partial<UiPrefs>) ?? {}) };
    if (!Array.isArray(ui.columns) || ui.columns.length === 0) ui.columns = defaultColumns;
    const lang: Lang = settings.language ?? info.language ?? i18nLang();
    setI18nLang(lang);
    applyTheme(ui.theme);
    watchSystemTheme(get, set);
    set({
      lang,
      theme: ui.theme,
      resolvedTheme: resolveTheme(ui.theme),
      providers: { ...defaultProviders, ...(settings.providers ?? {}) },
      geoDb: settings.geoDb ?? { autoUpdate: true, active: null, urlTemplate: defaultUrlTemplate },
      updateRepo: settings.updateRepo ?? info.repo ?? 'extraut/MTRTracer',
      ready: true,
      info,
      options: { ...defaultOptions, ...settings.options },
      recent: settings.recent ?? [],
      ui,
      geoStatus: geo,
      selectedAdapterId: settings.adapterId,
      vpnMode: settings.vpnMode ?? false,
      vpnServer: settings.vpnServer ?? null,
      originSetting: settings.origin ?? { mode: 'auto', lat: null, lon: null, label: null },
    });
    request('window.state').then((s) => set({ maximized: s.maximized })).catch(() => undefined);
    request('geo.self', {}).then((origin) => set({ origin })).catch(() => undefined);
    await get().loadAdapters();
  },

  async loadAdapters() {
    set({ adaptersLoading: true });
    try {
      const res = await request('adapters.list');
      set({ adapters: res.adapters, selectedAdapterId: res.selectedId, adaptersLoading: false, tunnels: res.tunnels ?? [], vpnMode: res.vpnMode ?? get().vpnMode });
    } catch (e) {
      set({ adaptersLoading: false });
      get().notify((e as Error).message, 'error');
    }
  },

  openAdapterGate(open) {
    set({ adapterGateOpen: open });
    if (open) void get().loadAdapters();
    void request('adapters.watch', { on: open });
  },

  async chooseAdapter(id, vpnMode) {
    const res = await request('adapters.select', { adapterId: id, vpnMode: vpnMode ?? get().vpnMode });
    set({ selectedAdapterId: id, adapterGateOpen: false, vpnMode: res.vpnMode });
    void request('adapters.watch', { on: false });
    request('geo.self', { force: true }).then((origin) => set({ origin })).catch(() => undefined);
  },

  setView(view) {
    set({ view });
  },

  async setOrigin(o) {
    set({ originSetting: o });
    await request('settings.set', { origin: o });
    if (o.mode === 'auto') request('geo.self', { force: true }).then((origin) => set({ origin })).catch(() => undefined);
  },

  async setVpnServer(v) {
    set({ vpnServer: v });
    await request('settings.set', { vpnServer: v ?? '' });
  },

  async setVpnMode(v) {
    set({ vpnMode: v });
    await request('settings.set', { vpnMode: v });
  },

  async start(target) {
    const t = target.trim();
    if (!t) return;
    probeBuffer.length = 0;
    series.clear();
    set({ hops: [], destTtl: null, round: 0, elapsedMs: 0, tcp: null, vpn: null, notes: [], resolved: null, routeGeo: null, previousRun: null, probeVersion: get().probeVersion + 1, selectedTtl: null, view: 'trace' });
    try {
      await request('trace.start', { target: t, options: get().options, vpnMode: get().vpnMode });
      set({ recent: [t, ...get().recent.filter((r) => r.toLowerCase() !== t.toLowerCase())].slice(0, 12) });
    } catch (e) {
      get().notify((e as Error).message, 'error');
    }
  },

  async stop() {
    await request('trace.stop');
  },

  async pause(paused) {
    await request('trace.pause', { paused });
  },

  async reset() {
    const input = get().session?.overlayInput ?? get().session?.input;
    if (input) await get().start(input);
  },

  setOptions(patch) {
    const options = { ...get().options, ...patch };
    set({ options });
    persist(get);
  },

  setUi(patch) {
    set({ ui: { ...get().ui, ...patch } });
    persist(get);
  },

  setHover(ttl) {
    if (get().hoverTtl !== ttl) set({ hoverTtl: ttl });
  },

  setSelected(ttl) {
    set({ selectedTtl: get().selectedTtl === ttl ? null : ttl });
  },

  async setLang(l) {
    setI18nLang(l);
    set({ lang: l, ui: { ...get().ui, lang: l } });
    await request('settings.set', { language: l });
    persist(get);
  },

  setTheme(t) {
    applyTheme(t);
    set({ theme: t, resolvedTheme: resolveTheme(t), ui: { ...get().ui, theme: t } });
    persist(get);
  },

  openSettings(tab) {
    set({ settingsTab: tab });
  },

  async setProviders(p) {
    const providers = { ...get().providers, ...p };
    set({ providers });
    await request('settings.set', { providers });
  },

  async setGeoDb(p) {
    const geoDb = { ...get().geoDb, ...p };
    set({ geoDb });
    await request('settings.set', { geoDb });
  },

  async setUpdateRepo(repo) {
    const res = await request('settings.set', { updateRepo: repo });
    set({ updateRepo: res.updateRepo ?? repo, update: null, updateAvailable: false });
  },

  async checkUpdate() {
    set({ updateChecking: true });
    try {
      const update = await request('app.checkUpdate');
      set({ update, updateAvailable: update.newerAvailable });
    } catch (e) {
      get().notify((e as Error).message, 'error');
    } finally {
      set({ updateChecking: false });
    }
  },

  notify(text, level = 'info') {
    const id = ++toastId;
    set({ toast: { id, text, level } });
    window.setTimeout(() => {
      if (get().toast?.id === id) set({ toast: null });
    }, level === 'error' ? 6000 : 3200);
  },
}));

function persist(get: () => Store) {
  window.clearTimeout(saveTimer);
  saveTimer = window.setTimeout(() => {
    const { options, ui } = get();
    void request('settings.set', { options, ui });
  }, 400);
}

let subscribed = false;
let probeFlush: number | undefined;

function subscribe(set: (p: Partial<Store> | ((s: Store) => Partial<Store>)) => void, get: () => Store) {
  if (subscribed) return;
  subscribed = true;

  on('trace.state', (s) => {
    const prev = get().session;
    set({ session: s });
    if (prev?.sessionId !== s.sessionId) set({ events: [] });
  });
  on('trace.resolved', (r) => set({ resolved: r }));
  on('trace.hops', (snap) => {
    if (get().session && snap.sessionId !== get().session!.sessionId) return;
    set({ hops: snap.hops, destTtl: snap.destTtl, round: snap.round, elapsedMs: snap.elapsedMs, tcp: snap.tcp, notes: snap.notes, vpn: snap.vpn ?? null });
  });
  on('trace.probes', ({ rows }) => {
    pushProbes(rows);
    // Coalesce re-renders of the log to one per frame.
    if (probeFlush === undefined)
      probeFlush = requestAnimationFrame(() => {
        probeFlush = undefined;
        set((s) => ({ probeVersion: s.probeVersion + 1 }));
      });
  });
  on('trace.event', (e) => set((s) => ({ events: [...s.events.slice(-1999), e] })));
  on('host.info', (h) => set((s) => ({ hosts: { ...s.hosts, [h.ip]: h } })));
  on('geo.status', (g) => set({ geoStatus: g }));
  on('adapters.traffic', ({ t, items }) => {
    const prev = get().traffic;
    const next: Store['traffic'] = {};
    for (const it of items) {
      const p = prev[it.id];
      const dt = p ? (t - p.t) / 1000 : 0;
      next[it.id] = {
        t,
        sent: it.sent,
        recv: it.recv,
        upBps: p && dt > 0 ? Math.max(0, ((it.sent - p.sent) * 8) / dt) : 0,
        downBps: p && dt > 0 ? Math.max(0, ((it.recv - p.recv) * 8) / dt) : 0,
      };
    }
    set({ traffic: next });
  });
  on('connections.snapshot', ({ items }) => set({ connections: items }));
  on('connections.events', ({ t, opened, closed }) =>
    set((s) => ({
      connLog: [
        ...s.connLog,
        ...opened.map((c) => ({ t, kind: 'open' as const, c })),
        ...closed.map((c) => ({ t, kind: 'close' as const, c })),
      ].slice(-3000),
    })),
  );
  on('window.state', ({ maximized }) => set({ maximized }));
  on('route.geo', (g) => {
    if (get().session && g.sessionId !== get().session!.sessionId) return;
    set({ routeGeo: g });
  });
  on('geo.origin', (origin) => set({ origin }));
  on('history.previous', ({ sessionId, run }) => {
    if (get().session?.sessionId === sessionId) set({ previousRun: run });
  });
}
