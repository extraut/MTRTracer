import { L, locale } from '../i18n';
/** Milliseconds with precision that matches magnitude: 0.94, 4.21, 32.4, 145. */
export function ms(v: number | null | undefined): string {
  if (v == null || Number.isNaN(v)) return '—';
  if (v < 10) return v.toFixed(2);
  if (v < 100) return v.toFixed(1);
  return Math.round(v).toString();
}

export function pct(v: number | null | undefined): string {
  if (v == null) return '—';
  if (v === 0) return '0';
  if (v < 10) return v.toFixed(1);
  return Math.round(v).toString();
}

/** Latency bucket for color: 0 good … 4 bad. */
export function latencyLevel(v: number | null | undefined): number {
  if (v == null) return -1;
  if (v < 30) return 0;
  if (v < 60) return 1;
  if (v < 100) return 2;
  if (v < 180) return 3;
  return 4;
}

export const latencyVar = (v: number | null | undefined) => {
  const l = latencyLevel(v);
  return l < 0 ? 'var(--text-4)' : `var(--lat-${l})`;
};

/** sRGB equivalents of the latency scale for canvas/WebGL consumers that do not parse oklch (updated per theme). */
export const latencyMuted = { value: '#5b5e6b' };
export const latencyHex = ['#57d29a', '#b4d860', '#ecbf4f', '#ee8c4b', '#ec5d55'];
export const latencyColorHex = (v: number | null | undefined) => {
  const l = latencyLevel(v);
  return l < 0 ? latencyMuted.value : latencyHex[l];
};

export function lossLevel(loss: number): 'ok' | 'warn' | 'bad' {
  if (loss <= 0) return 'ok';
  if (loss < 5) return 'warn';
  return 'bad';
}

export function bits(bps: number): string {
  if (bps < 1000) return `${Math.round(bps)} ${L('бит/с', 'bit/s')}`;
  if (bps < 1e6) return `${(bps / 1e3).toFixed(bps < 1e4 ? 1 : 0)} ${L('Кбит/с', 'kbit/s')}`;
  if (bps < 1e9) return `${(bps / 1e6).toFixed(bps < 1e7 ? 1 : 0)} ${L('Мбит/с', 'Mbit/s')}`;
  return `${(bps / 1e9).toFixed(1)} ${L('Гбит/с', 'Gbit/s')}`;
}

export function linkSpeed(bps: number): string | null {
  if (!bps || bps <= 0) return null;
  if (bps >= 1e9) return `${+(bps / 1e9).toFixed(1)} ${L('Гбит/с', 'Gbit/s')}`;
  if (bps >= 1e6) return `${Math.round(bps / 1e6)} ${L('Мбит/с', 'Mbit/s')}`;
  return `${Math.round(bps / 1e3)} ${L('Кбит/с', 'kbit/s')}`;
}

export function bytes(n: number): string {
  if (n < 1024) return `${n} ${L('Б', 'B')}`;
  if (n < 1024 ** 2) return `${(n / 1024).toFixed(1)} ${L('КБ', 'KB')}`;
  if (n < 1024 ** 3) return `${(n / 1024 ** 2).toFixed(1)} ${L('МБ', 'MB')}`;
  return `${(n / 1024 ** 3).toFixed(2)} ${L('ГБ', 'GB')}`;
}

export function duration(msTotal: number): string {
  const s = Math.floor(msTotal / 1000);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  const mm = String(m).padStart(2, '0');
  const ss = String(sec).padStart(2, '0');
  return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
}

export function clock(t: number, withMs = true): string {
  const d = new Date(t);
  const base = d.toLocaleTimeString(locale(), { hour12: false });
  return withMs ? `${base}.${String(d.getMilliseconds()).padStart(3, '0')}` : base;
}

export function km(v: number): string {
  if (v < 10) return `${v.toFixed(1)} ${L('км', 'km')}`;
  return `${Math.round(v).toLocaleString(locale())} ${L('км', 'km')}`;
}

export function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10;
  const m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
  return many;
}

const regionNamesCache = new Map<string, Intl.DisplayNames | null>();
const regionNames = () => {
  const l = locale();
  if (!regionNamesCache.has(l)) {
    try {
      regionNamesCache.set(l, new Intl.DisplayNames([l], { type: 'region' }));
    } catch {
      regionNamesCache.set(l, null);
    }
  }
  return regionNamesCache.get(l)!;
};

export function countryName(cc: string | null | undefined): string | null {
  if (!cc) return null;
  try {
    return regionNames()?.of(cc.toUpperCase()) ?? cc;
  } catch {
    return cc;
  }
}

/** Read through a proxy so the current language is used at every access. */
export const statusText: Record<string, string> = new Proxy({} as Record<string, string>, {
  get: (_t, key: string | symbol) =>
    (({
      ok: L('ответ цели', 'target reply'),
      'ttl-expired': L('TTL истёк', 'TTL expired'),
      timeout: L('таймаут', 'timeout'),
      'net-unreachable': L('сеть недоступна', 'network unreachable'),
      'host-unreachable': L('узел недоступен', 'host unreachable'),
      'proto-unreachable': L('протокол недоступен', 'protocol unreachable'),
      'port-unreachable': L('порт недоступен', 'port unreachable'),
      prohibited: L('запрещено фильтром', 'prohibited by filter'),
      'packet-too-big': L('пакет больше MTU', 'packet larger than MTU'),
      'param-problem': L('ошибка параметров', 'parameter problem'),
      'source-quench': 'source quench',
      'bad-route': L('неверный маршрут', 'bad route'),
      'no-resources': L('нет ресурсов', 'no resources'),
      error: L('ошибка', 'error'),
      pending: L('ожидание', 'pending'),
      'tcp-open': L('TCP: порт открыт', 'TCP: port open'),
      'tcp-refused': L('TCP: отказ (RST)', 'TCP: refused (RST)'),
      'tcp-timeout': L('TCP: таймаут', 'TCP: timeout'),
    }) as Record<string, string>)[key as string],
});
