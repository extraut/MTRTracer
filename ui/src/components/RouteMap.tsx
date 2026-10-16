import { L, plural } from '../i18n';
import { palette } from '../theme';
import { useEffect, useMemo, useRef, useState } from 'react';
import maplibregl, { type GeoJSONSource, type Map as MLMap, type StyleSpecification } from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { feature } from 'topojson-client';
import type { Feature, FeatureCollection, LineString } from 'geojson';
import world50 from 'world-atlas/countries-50m.json';
import world110 from 'world-atlas/countries-110m.json';
import type { Hop, HostInfo, RouteGeo, VpnBlock } from '../bridge/protocol';
import { useStore } from '../store';
import { distanceKm, facing, greatCircle } from '../lib/geodesy';
import { km, latencyColorHex, latencyHex, ms } from '../lib/format';
import { Crosshair, Globe, Layers, MapIcon } from './Icons';
import './map.css';

// Antarctica is dropped: no routes go there and its polygon touching the pole causes globe artifacts.
function countries(topo: unknown): FeatureCollection {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const t = topo as any;
  const fc = feature(t, t.objects.countries) as unknown as FeatureCollection;
  return { type: 'FeatureCollection', features: fc.features.filter((f) => String(f.id) !== '010' && (f.properties as { name?: string })?.name !== 'Antarctica') };
}
const land50 = countries(world50);
const land110 = countries(world110);

/** Theme colours resolved for MapLibre and canvas (the map remounts when the theme changes). */
const C = palette;

const offlineStyle = (): StyleSpecification => ({
  version: 8,
  name: 'mtr-offline',
  sources: { land110: { type: 'geojson', data: land110 }, land50: { type: 'geojson', data: land50 } },
  layers: [
    { id: 'bg', type: 'background', paint: { 'background-color': C.ocean } },
    { id: 'land-lo', type: 'fill', source: 'land110', maxzoom: 3, paint: { 'fill-color': C.land } },
    { id: 'land-hi', type: 'fill', source: 'land50', minzoom: 3, paint: { 'fill-color': C.land } },
    { id: 'borders-lo', type: 'line', source: 'land110', maxzoom: 3, paint: { 'line-color': C.border, 'line-width': 0.5 } },
    { id: 'borders-hi', type: 'line', source: 'land50', minzoom: 3, paint: { 'line-color': C.border, 'line-width': ['interpolate', ['linear'], ['zoom'], 3, 0.6, 7, 1.2] } },
  ],
});
/** OpenFreeMap has no dark/light switch per request: light themes get the "positron" style. */
const onlineStyleUrl = () => `https://tiles.openfreemap.org/styles/${palette.dark ? 'dark' : 'positron'}`;
const empty: FeatureCollection = { type: 'FeatureCollection', features: [] };

type NodeKind = 'origin' | 'hop' | 'server' | 'target';
interface MapNode {
  key: string;
  kind: NodeKind;
  ttls: number[];
  ip: string | null;
  lon: number;
  lat: number;
  label: string;
  sub: string;
  status: string;
  isDest: boolean;
  loss: number;
  avg: number | null;
}
interface Segment {
  key: string;
  from: MapNode;
  to: MapNode;
  coords: [number, number][];
  color: string;
  style: 'solid' | 'lossy' | 'gap' | 'tunnel';
  gap: number;
}

/** Builds drawable nodes from the backend's verified placement; nothing unverified is drawn. */
function buildNodes(geo: RouteGeo | null, hops: Hop[], hosts: Record<string, HostInfo>, vpn: VpnBlock | null): { nodes: MapNode[]; gaps: Map<string, number> } {
  const nodes: MapNode[] = [];
  const gaps = new Map<string, number>();
  if (!geo) return { nodes, gaps };
  if (geo.origin) {
    nodes.push({
      key: 'origin', kind: 'origin', ttls: [], ip: null, lon: geo.origin.lon, lat: geo.origin.lat, label: L('Вы', 'You'),
      sub: geo.origin.label ?? '', status: 'confirmed', isDest: false, loss: 0, avg: null,
    });
  }
  let pendingGap = 0;
  for (const g of geo.hops) {
    const hop = hops.find((h) => h.ttl === g.ttl);
    if (g.status === 'local') {
      if (nodes[0]?.kind === 'origin') nodes[0].ttls.push(g.ttl);
      continue;
    }
    // Every located hop is drawn, including ones whose place contradicts the RTT (marked "?").
    if (g.lat == null || g.lon == null || !['confirmed', 'single', 'anycast', 'unresolved'].includes(g.status)) {
      if (g.status === 'unresolved') pendingGap++;
      continue;
    }
    const info = g.ip ? hosts[g.ip] : undefined;
    const last = nodes[nodes.length - 1];
    const isDest = !!hop?.isDestination;
    if (last && distanceKm(last, { lat: g.lat, lon: g.lon }) < 25) {
      if (last.kind !== 'origin') {
        last.ttls.push(g.ttl);
        last.ip = g.ip;
        last.label = info?.hostname ?? g.ip ?? last.label;
        last.loss = hop?.loss ?? 0;
        last.avg = hop?.avg ?? null;
        last.isDest = isDest;
        last.status = g.status;
      } else last.ttls.push(g.ttl);
      continue;
    }
    const node: MapNode = {
      key: `${g.ttl}:${g.ip}`, kind: 'hop', ttls: [g.ttl], ip: g.ip, lon: g.lon, lat: g.lat,
      label: info?.hostname ?? g.ip ?? '', sub: [g.city, g.countryCode].filter(Boolean).join(', '),
      status: g.status, isDest, loss: hop?.loss ?? 0, avg: hop?.avg ?? null,
    };
    if (pendingGap > 0 && last) gaps.set(`${last.key}>${node.key}`, pendingGap);
    pendingGap = 0;
    nodes.push(node);
  }
  if (vpn) {
    const server = nodes[nodes.length - 1];
    if (server && server.kind !== 'origin') {
      server.kind = 'server';
      server.isDest = false;
      server.label = `${L('Сервер VPN', 'VPN server')} · ${server.ip}`;
    }
    const t = vpn.targetGeo;
    if (t && t.lat != null && t.lon != null) {
      nodes.push({
        key: 'vpn-target', kind: 'target', ttls: [], ip: vpn.targetIp, lon: t.lon, lat: t.lat, label: vpn.target,
        sub: [t.city, t.countryCode].filter(Boolean).join(', '), status: t.status, isDest: true, loss: vpn.loss, avg: vpn.avg,
      });
    }
  }
  return { nodes, gaps };
}

interface Comet {
  start: number;
  duration: number;
  marks: number[];
  stopAt: number;
  lost: boolean;
  key: string;
}

export function RouteMap() {
  const box = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  const mapRef = useRef<MLMap | null>(null);
  const markers = useRef(new Map<string, { marker: maplibregl.Marker; el: HTMLDivElement; lastRecv: number }>());
  const segments = useRef<Segment[]>([]);
  const reveal = useRef(new Map<string, number>());
  const comets = useRef<Comet[]>([]);
  const raf = useRef(0);
  const userMoved = useRef(false);
  const lastFitCount = useRef(0);
  const [loaded, setLoaded] = useState(false);

  const hops = useStore((s) => s.hops);
  const hosts = useStore((s) => s.hosts);
  const geo = useStore((s) => s.routeGeo);
  const vpn = useStore((s) => s.vpn);
  const round = useStore((s) => s.round);
  const sessionId = useStore((s) => s.session?.sessionId);
  const projection = useStore((s) => s.ui.projection);
  const detail = useStore((s) => s.ui.mapDetail);
  const setUi = useStore((s) => s.setUi);
  const hoverTtl = useStore((s) => s.hoverTtl);
  const selectedTtl = useStore((s) => s.selectedTtl);
  const geoStatus = useStore((s) => s.geoStatus);

  const { nodes, gaps } = useMemo(() => buildNodes(geo, hops, hosts, vpn), [geo, hops, hosts, vpn]);
  const routeKey = nodes.map((n) => `${n.key}@${n.lat.toFixed(2)},${n.lon.toFixed(2)}`).join('|');

  // ---------- map lifecycle ----------
  useEffect(() => {
    const map = new maplibregl.Map({
      container: box.current!,
      style: offlineStyle(),
      center: [30, 52],
      zoom: 2.2,
      attributionControl: false,
      renderWorldCopies: false,
      fadeDuration: 0,
      maxPitch: 0,
      dragRotate: false,
      pitchWithRotate: false,
    });
    map.touchZoomRotate.disableRotation();
    mapRef.current = map;
    map.on('style.load', () => {
      addRouteLayers(map);
      try {
        map.setProjection({ type: useStore.getState().ui.projection });
      } catch {
        /* renderer without globe */
      }
      setLoaded(true);
      pushRoute(map, segments.current);
    });
    // The overlay follows the camera: redraw on every rendered frame while the map moves.
    map.on('render', () => draw(performance.now()));
    map.on('dragstart', (e) => e.originalEvent && (userMoved.current = true));
    map.on('wheel', () => (userMoved.current = true));
    const ro = new ResizeObserver(() => {
      map.resize();
      sizeCanvas();
      draw(performance.now());
    });
    ro.observe(box.current!);
    sizeCanvas();
    return () => {
      ro.disconnect();
      cancelAnimationFrame(raf.current);
      map.remove();
      mapRef.current = null;
      markers.current.clear();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !loaded) return;
    try {
      map.setProjection({ type: projection });
    } catch {
      /* ignore */
    }
  }, [projection, loaded]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    map.setStyle(detail ? onlineStyleUrl() : offlineStyle(), { diff: false });
  }, [detail]);

  useEffect(() => {
    userMoved.current = false;
    lastFitCount.current = 0;
    comets.current = [];
    reveal.current.clear();
    draw(performance.now());
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId]);

  // ---------- route geometry: static layers change only when the route changes ----------
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !loaded) return;
    const prev = new Map(segments.current.map((s) => [s.key, s]));
    const now = performance.now();
    const next: Segment[] = [];
    for (let i = 1; i < nodes.length; i++) {
      const from = nodes[i - 1];
      const to = nodes[i];
      const key = `${from.key}>${to.key}`;
      const tunnel = to.kind === 'target';
      const gap = gaps.get(key) ?? 0;
      const old = prev.get(key);
      const coords = old && old.from.lat === from.lat && old.to.lat === to.lat ? old.coords : greatCircle([from.lon, from.lat], [to.lon, to.lat], 48);
      if (!old) reveal.current.set(key, now + next.length * 90);
      next.push({
        key, from, to, coords,
        color: tunnel ? C.accent : latencyColorHex(to.avg),
        style: tunnel ? 'tunnel' : gap > 0 ? 'gap' : to.loss >= 5 ? 'lossy' : 'solid',
        gap,
      });
    }
    segments.current = next;
    pushRoute(map, next);
    syncMarkers(map, nodes);
    kick();

    if (!userMoved.current && nodes.length > 0 && nodes.length !== lastFitCount.current) {
      lastFitCount.current = nodes.length;
      fitTo(map, nodes, 1000);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [routeKey, loaded, gaps]);

  // Marker colours and labels follow live stats without touching the static layers.
  useEffect(() => {
    if (mapRef.current && loaded) syncMarkers(mapRef.current, nodes);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hops, vpn]);

  // ---------- one packet per round ----------
  useEffect(() => {
    if (round === 0 || segments.current.length === 0 || document.visibilityState !== 'visible') return;
    const segs = segments.current;
    const deltas = segs.map((s) => Math.max(0.6, (s.to.avg ?? 0) - (s.from.avg ?? 0)));
    const total = deltas.reduce((a, b) => a + b, 0);
    const shares = deltas.map((d) => Math.max(0.07, d / total));
    const sum = shares.reduce((a, b) => a + b, 0);
    let acc = 0;
    const marks = shares.map((s) => (acc += s / sum));
    const destHop = hops.find((h) => h.isDestination);
    const lost = vpn ? vpn.history[vpn.history.length - 1] == null && vpn.sent > 0 : (destHop?.consecutiveLoss ?? 0) > 0;
    let stopAt = segs.length - 1;
    if (lost) {
      const firstBad = segs.findIndex((s) => s.style === 'lossy');
      stopAt = firstBad >= 0 ? firstBad : segs.length - 1;
    }
    const rtt = vpn?.avg ?? destHop?.avg ?? 40;
    comets.current = [...comets.current.slice(-3), { start: performance.now(), duration: Math.min(2600, Math.max(1100, rtt * 22)), marks, stopAt, lost, key: routeKey }];
    kick();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [round]);

  useEffect(() => {
    markers.current.forEach(({ el }) => {
      const ttls = (el.dataset.ttls ?? '').split(',').filter(Boolean).map(Number);
      el.classList.toggle('hl', hoverTtl != null && ttls.includes(hoverTtl));
      el.classList.toggle('sel', selectedTtl != null && ttls.includes(selectedTtl));
    });
  }, [hoverTtl, selectedTtl, routeKey]);

  function sizeCanvas() {
    const c = canvas.current;
    const b = box.current;
    if (!c || !b) return;
    const dpr = window.devicePixelRatio || 1;
    c.width = Math.round(b.clientWidth * dpr);
    c.height = Math.round(b.clientHeight * dpr);
    c.style.width = `${b.clientWidth}px`;
    c.style.height = `${b.clientHeight}px`;
  }

  function kick() {
    if (!raf.current) raf.current = requestAnimationFrame(tick);
  }

  function tick(now: number) {
    raf.current = 0;
    const busy = draw(now);
    if (busy) raf.current = requestAnimationFrame(tick);
  }

  /** Canvas overlay: reveal sweeps and packets. Returns true while something is animating. */
  function draw(now: number): boolean {
    const map = mapRef.current;
    const c = canvas.current;
    if (!map || !c) return false;
    const ctx = c.getContext('2d')!;
    const dpr = window.devicePixelRatio || 1;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, c.width, c.height);
    const globe = useStore.getState().ui.projection === 'globe';
    const center = map.getCenter();
    const proj = (p: [number, number]) => {
      if (globe && !facing(center, p[0], p[1])) return null;
      const pt = map.project(p);
      return [pt.x, pt.y] as const;
    };
    let busy = false;
    const segs = segments.current;

    // Reveal: a bright sweep runs along each new segment once.
    for (const s of segs) {
      const at = reveal.current.get(s.key);
      if (at == null) continue;
      const f = (now - at) / 650;
      if (f < 0) {
        busy = true;
        continue;
      }
      if (f >= 1) {
        reveal.current.delete(s.key);
        continue;
      }
      busy = true;
      const head = Math.floor(easeOut(f) * (s.coords.length - 1));
      ctx.lineWidth = 2.5;
      ctx.lineCap = 'round';
      ctx.strokeStyle = `rgba(${C.ring},${0.9 * (1 - f)})`;
      ctx.beginPath();
      let started = false;
      for (let i = Math.max(0, head - 10); i <= head; i++) {
        const p = proj(s.coords[i]);
        if (!p) {
          started = false;
          continue;
        }
        if (!started) ctx.moveTo(p[0], p[1]);
        else ctx.lineTo(p[0], p[1]);
        started = true;
      }
      ctx.stroke();
    }

    // Packets.
    comets.current = comets.current.filter((k) => now - k.start < k.duration + 450 && k.marks.length === segs.length);
    for (const k of comets.current) {
      busy = true;
      const p = (now - k.start) / k.duration;
      const end = k.marks[k.stopAt];
      const fade = p > end ? Math.max(0, 1 - (p - end) / (450 / k.duration)) : 1;
      const pts: (readonly [number, number] | null)[] = [];
      for (let i = 0; i <= 12; i++) {
        const q = Math.min(p, end) - 0.08 * (1 - i / 12);
        if (q < 0) continue;
        pts.push(proj(pointOnRoute(segs, k.marks, q)));
      }
      const color = k.lost && p > end ? C.lostRgb : C.comet;
      for (let i = 1; i < pts.length; i++) {
        const a = pts[i - 1];
        const b = pts[i];
        if (!a || !b) continue;
        ctx.strokeStyle = `rgba(${color},${(i / pts.length) * 0.85 * fade})`;
        ctx.lineWidth = 1 + (i / pts.length) * 2.2;
        ctx.beginPath();
        ctx.moveTo(a[0], a[1]);
        ctx.lineTo(b[0], b[1]);
        ctx.stroke();
      }
      const headPt = pts[pts.length - 1];
      if (headPt) {
        ctx.fillStyle = `rgba(${color},${0.25 * fade})`;
        ctx.beginPath();
        ctx.arc(headPt[0], headPt[1], 7, 0, Math.PI * 2);
        ctx.fill();
        ctx.fillStyle = `rgba(${k.lost && p >= end ? C.lostRgb : C.comet},${fade})`;
        ctx.beginPath();
        ctx.arc(headPt[0], headPt[1], 3.4, 0, Math.PI * 2);
        ctx.fill();
      }
    }
    return busy;
  }

  function syncMarkers(map: MLMap, list: MapNode[]) {
    const seen = new Set<string>();
    const st = useStore.getState();
    for (const n of list) {
      seen.add(n.key);
      let m = markers.current.get(n.key);
      if (!m) {
        const el = document.createElement('div');
        el.className = 'node';
        el.innerHTML = '<span class="node-ring"></span><span class="node-dot"></span><span class="node-label"><b></b><i></i></span>';
        el.addEventListener('mouseenter', () => {
          const t = Number(el.dataset.ttls?.split(',').filter(Boolean).pop());
          if (t > 0) useStore.getState().setHover(t);
        });
        el.addEventListener('mouseleave', () => useStore.getState().setHover(null));
        el.addEventListener('click', () => {
          const t = Number(el.dataset.ttls?.split(',').filter(Boolean).pop());
          if (t > 0) useStore.getState().setSelected(t);
        });
        const marker = new maplibregl.Marker({ element: el, anchor: 'center' }).setLngLat([n.lon, n.lat]).addTo(map);
        try {
          // Hide markers on the far side of the globe.
          (marker as unknown as { setOpacity: (a: string, b: string) => void }).setOpacity('1', '0');
        } catch {
          /* older API */
        }
        m = { marker, el, lastRecv: -1 };
        markers.current.set(n.key, m);
      }
      const { el } = m;
      m.marker.setLngLat([n.lon, n.lat]);
      el.dataset.ttls = n.ttls.join(',');
      el.className = `node k-${n.kind} s-${n.status}${n.isDest ? ' dest' : ''}${n.loss >= 5 ? ' lossy' : ''}`;
      el.style.setProperty('--c', n.kind === 'origin' ? C.accent : n.kind === 'server' ? C.server : n.loss >= 20 ? C.lost : latencyColorHex(n.avg));
      const ttlText = n.ttls.length === 0 ? '' : `${n.ttls.length > 1 ? `${n.ttls[0]}–${n.ttls[n.ttls.length - 1]}` : n.ttls[0]} · `;
      el.querySelector('.node-label b')!.textContent = n.kind === 'origin' ? L('Вы', 'You') : `${n.kind === 'hop' ? ttlText : ''}${n.label}`;
      const statusText = n.status === 'anycast' ? 'anycast · ' : n.status === 'single' ? '≈ ' : n.status === 'unresolved' ? '? ' : '';
      el.querySelector('.node-label i')!.textContent =
        n.kind === 'origin' ? n.sub : `${statusText}${n.sub}${n.avg != null ? ` · ${ms(n.avg)} ${L('мс', 'ms')}` : ''}${n.loss > 0 ? ` · ${n.loss}%` : ''}`;
      const lastTtl = n.ttls[n.ttls.length - 1];
      const recv = n.kind === 'target' ? (st.vpn?.recv ?? 0) : (st.hops.find((h) => h.ttl === lastTtl)?.recv ?? 0);
      if (recv !== m.lastRecv && m.lastRecv >= 0) {
        el.classList.remove('ping');
        void el.offsetWidth;
        el.classList.add('ping');
      }
      m.lastRecv = recv;
    }
    markers.current.forEach((m, key) => {
      if (!seen.has(key)) {
        m.marker.remove();
        markers.current.delete(key);
      }
    });
  }

  const summary = geo?.summary;
  const dbBusy = geoStatus && (geoStatus.db === 'downloading' || geoStatus.db === 'updating');
  const placed = nodes.filter((n) => n.kind !== 'origin').length;

  return (
    <div className="map-wrap">
      <div ref={box} className="map" />
      <canvas ref={canvas} className="map-overlay" aria-hidden />
      <div className="map-top">
        <div className="map-summary">
          {!geo || hops.length === 0 ? (
            <span className="muted">{hops.length ? L('Определяем местоположение узлов…', 'Locating routers…') : L('Маршрут появится здесь', 'The route will appear here')}</span>
          ) : (
            <>
              <span>
                <b className="num">{placed}</b> {plural(placed, ['точка', 'точки', 'точек'], ['point', 'points'])}
              </span>
              {summary && summary.pathKm > 0 && (
                <>
                  <span className="sep" />
                  <span title={L('Сумма длин участков между подтверждёнными точками', 'Sum of the segment lengths between confirmed points')}>
                    {L('путь', 'path')} <b className="num">{km(summary.pathKm)}</b>
                  </span>
                </>
              )}
              {summary?.directKm != null && summary.minRttMs != null && summary.directKm > 50 && (
                <>
                  <span className="sep" />
                  <span title={L('Минимальный RTT по прямой при скорости света в оптоволокне (~200 000 км/с)', 'Minimum RTT in a straight line at the speed of light in fibre (~200,000 km/s)')}>
                    {L('по прямой', 'straight line')} {km(summary.directKm)}: {L('минимум', 'minimum')} <b className="num">{ms(summary.minRttMs)}</b>, {L('измерено', 'measured')}{' '}
                    <b className="num">{ms(summary.measuredMs)}</b> {L('мс', 'ms')}
                    {summary.stretch != null && <span className="muted"> (×{summary.stretch.toFixed(1)})</span>}
                  </span>
                </>
              )}
              {summary && summary.unresolved > 0 && (
                <>
                  <span className="sep" />
                  <span className="warn-text" title={L('Место этих узлов по данным источников не сходится с измеренной задержкой. Они всё равно показаны на карте, с пометкой «?»', 'The sources put these routers where the measured latency cannot reach. They are still shown on the map, marked with "?"')}>
                    {summary.unresolved} {plural(summary.unresolved, ['узел', 'узла', 'узлов'], ['router', 'routers'])} {summary.unresolved === 1 ? L('не сходится с RTT', 'does not match RTT') : L('не сходятся с RTT', 'do not match RTT')}
                  </span>
                </>
              )}
              {vpn && (
                <>
                  <span className="sep" />
                  <span>
                    {L('через VPN', 'via VPN')}{vpn.exit?.city ? `, ${L('выход', 'exit')}: ${vpn.exit.city}` : ''}
                    {vpn.exit?.colo ? ` (Cloudflare ${vpn.exit.colo})` : ''}
                  </span>
                </>
              )}
            </>
          )}
        </div>
        <div className="map-ctl">
          <button className={`btn sm ${projection === 'mercator' ? 'active' : ''}`} onClick={() => setUi({ projection: 'mercator' })} title={L('Плоская карта', 'Flat map')}>
            <MapIcon size={14} />
          </button>
          <button className={`btn sm ${projection === 'globe' ? 'active' : ''}`} onClick={() => setUi({ projection: 'globe' })} title={L('Глобус', 'Globe')}>
            <Globe size={14} />
          </button>
          <button className={`btn sm ${detail ? 'active' : ''}`} onClick={() => setUi({ mapDetail: !detail })} title={L('Подробная подложка (онлайн, OpenFreeMap)', 'Detailed base map (online, OpenFreeMap)')}>
            <Layers size={14} />
          </button>
          <button
            className="btn sm"
            onClick={() => {
              userMoved.current = false;
              if (mapRef.current) fitTo(mapRef.current, nodes, 700);
            }}
            title={L('Показать весь маршрут', 'Show the whole route')}
            disabled={nodes.length === 0}
          >
            <Crosshair size={14} />
          </button>
        </div>
      </div>
      <div className="map-bottom">
        <div className="legends">
          <div className="legend" aria-label={L('Шкала задержки', 'Latency scale')}>
            {latencyHex.map((c, i) => (
              <span key={c} style={{ background: c }} title={['< 30', '30–60', '60–100', '100–180', '> 180'][i] + ' ' + L('мс', 'ms')} />
            ))}
            <em>0</em>
            <em>30</em>
            <em>60</em>
            <em>100</em>
            <em>180+ {L('мс', 'ms')}</em>
          </div>
          <div className="legend-conf">
            <span>
              <i className="lc confirmed" /> {L('подтверждено', 'confirmed')}
            </span>
            <span>
              <i className="lc single" /> {L('один источник', 'single source')}
            </span>
            <span>
              <i className="lc anycast" /> anycast
            </span>
            <span>
              <i className="lc unresolved" /> {L('не сходится с RTT', 'does not match RTT')}
            </span>
          </div>
        </div>
        <div className="attrib">
          {dbBusy && <span className="geo-dl">{L('Загрузка гео-базы', 'Downloading geo DB')} {Math.round((geoStatus!.progress ?? 0) * 100)}%</span>}
          {detail ? '© OpenFreeMap © OpenMapTiles © OSM · ' : '© Natural Earth · '}
          {L('геоданные', 'geo data')}: RIPE IPmap, MaxMind GeoLite, DB-IP, Cloudflare
        </div>
      </div>
    </div>
  );
}

const easeOut = (t: number) => 1 - Math.pow(1 - t, 4);

function positionAt(seg: Segment, f: number): [number, number] {
  const c = seg.coords;
  const x = Math.max(0, Math.min(1, f)) * (c.length - 1);
  const i = Math.floor(x);
  const t = x - i;
  const a = c[i];
  const b = c[Math.min(c.length - 1, i + 1)];
  return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t];
}

function pointOnRoute(segs: Segment[], marks: number[], q: number): [number, number] {
  let i = marks.findIndex((m) => q <= m);
  if (i < 0) i = segs.length - 1;
  const start = i === 0 ? 0 : marks[i - 1];
  return positionAt(segs[i], (q - start) / Math.max(1e-6, marks[i] - start));
}

function fitTo(map: MLMap, nodes: MapNode[], duration: number) {
  if (nodes.length === 0) return;
  if (nodes.length === 1) {
    map.easeTo({ center: [nodes[0].lon, nodes[0].lat], zoom: 4, duration });
    return;
  }
  const b = new maplibregl.LngLatBounds();
  nodes.forEach((n) => b.extend([n.lon, n.lat]));
  map.fitBounds(b, { padding: { top: 80, bottom: 70, left: 60, right: 60 }, maxZoom: 6, duration });
}

function addRouteLayers(map: MLMap) {
  if (map.getSource('route')) return;
  map.addSource('route', { type: 'geojson', data: empty });
  map.addLayer({
    id: 'route-glow',
    type: 'line',
    source: 'route',
    filter: ['==', ['get', 'style'], 'solid'],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': ['get', 'color'], 'line-width': 7, 'line-opacity': 0.12, 'line-blur': 3 },
  });
  map.addLayer({
    id: 'route-solid',
    type: 'line',
    source: 'route',
    filter: ['==', ['get', 'style'], 'solid'],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': ['get', 'color'], 'line-width': 2, 'line-opacity': 0.92 },
  });
  map.addLayer({
    id: 'route-lossy',
    type: 'line',
    source: 'route',
    filter: ['==', ['get', 'style'], 'lossy'],
    paint: { 'line-color': ['get', 'color'], 'line-width': 2, 'line-opacity': 0.92, 'line-dasharray': [2, 1.5] },
  });
  map.addLayer({
    id: 'route-gap',
    type: 'line',
    source: 'route',
    filter: ['==', ['get', 'style'], 'gap'],
    paint: { 'line-color': C.gap, 'line-width': 1.6, 'line-opacity': 0.9, 'line-dasharray': [1, 2] },
  });
  map.addLayer({
    id: 'route-tunnel',
    type: 'line',
    source: 'route',
    filter: ['==', ['get', 'style'], 'tunnel'],
    paint: { 'line-color': C.accent, 'line-width': 2, 'line-opacity': 0.85, 'line-dasharray': [3, 2] },
  });
}

function pushRoute(map: MLMap, segs: Segment[]) {
  const src = map.getSource('route') as GeoJSONSource | undefined;
  if (!src) return;
  const features: Feature<LineString>[] = segs.map((s) => ({
    type: 'Feature',
    properties: { color: s.color, style: s.style },
    geometry: { type: 'LineString', coordinates: s.coords },
  }));
  src.setData({ type: 'FeatureCollection', features });
}
