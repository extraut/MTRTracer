import { L } from '../i18n';
const R = 6371;
const rad = (d: number) => (d * Math.PI) / 180;
const deg = (r: number) => (r * 180) / Math.PI;

export function distanceKm(a: { lat: number; lon: number }, b: { lat: number; lon: number }): number {
  const dLat = rad(b.lat - a.lat);
  const dLon = rad(b.lon - a.lon);
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(rad(a.lat)) * Math.cos(rad(b.lat)) * Math.sin(dLon / 2) ** 2;
  return 2 * R * Math.asin(Math.min(1, Math.sqrt(h)));
}

/** Great-circle interpolation with continuous longitudes (no wrap jumps across the antimeridian). */
export function greatCircle(a: [number, number], b: [number, number], steps = 64): [number, number][] {
  const [lon1, lat1] = [rad(a[0]), rad(a[1])];
  const [lon2, lat2] = [rad(b[0]), rad(b[1])];
  const d = 2 * Math.asin(Math.sqrt(Math.sin((lat2 - lat1) / 2) ** 2 + Math.cos(lat1) * Math.cos(lat2) * Math.sin((lon2 - lon1) / 2) ** 2));
  if (d < 1e-6) return [a, b];
  const pts: [number, number][] = [];
  let prevLon = a[0];
  for (let i = 0; i <= steps; i++) {
    const f = i / steps;
    const A = Math.sin((1 - f) * d) / Math.sin(d);
    const B = Math.sin(f * d) / Math.sin(d);
    const x = A * Math.cos(lat1) * Math.cos(lon1) + B * Math.cos(lat2) * Math.cos(lon2);
    const y = A * Math.cos(lat1) * Math.sin(lon1) + B * Math.cos(lat2) * Math.sin(lon2);
    const z = A * Math.sin(lat1) + B * Math.sin(lat2);
    let lon = deg(Math.atan2(y, x));
    const lat = deg(Math.atan2(z, Math.sqrt(x * x + y * y)));
    while (lon - prevLon > 180) lon -= 360;
    while (lon - prevLon < -180) lon += 360;
    prevLon = lon;
    pts.push([lon, lat]);
  }
  return pts;
}

/** Is a point on the visible hemisphere of a globe centred at `center`? */
export function facing(center: { lat: number; lng: number }, lon: number, lat: number): boolean {
  const c1 = Math.cos(rad(center.lat));
  const c2 = Math.cos(rad(lat));
  const dot = c1 * c2 * Math.cos(rad(lon - center.lng)) + Math.sin(rad(center.lat)) * Math.sin(rad(lat));
  return dot > 0.05;
}

/** Read through a proxy so the current language is used at every access. */
export const sourceLabel: Record<string, string> = new Proxy({} as Record<string, string>, {
  get: (_t, key: string | symbol) =>
    (({
      'ripe-ipmap': 'RIPE IPmap',
      'cf-colo': 'Cloudflare colo',
      hint: L('имя роутера', 'router name'),
      maxmind: 'MaxMind GeoLite (RIPEstat)',
      dbip: 'DB-IP Lite',
      ipapi: 'ip-api.com',
    }) as Record<string, string>)[key as string],
});

/** Read through a proxy so the current language is used at every access. */
export const statusLabel: Record<string, string> = new Proxy({} as Record<string, string>, {
  get: (_t, key: string | symbol) =>
    (({
      confirmed: L('подтверждено', 'confirmed'),
      single: L('один источник', 'single source'),
      anycast: 'anycast',
      unresolved: L('не сходится с RTT', 'does not match RTT'),
      pending: L('определяется…', 'resolving…'),
      local: L('локальная сеть', 'local network'),
      silent: L('нет ответа', 'no reply'),
    }) as Record<string, string>)[key as string],
});
