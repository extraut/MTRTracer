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

/** sRGB equivalents of the latency scale for canvas/WebGL consumers that do not parse oklch. */
export const latencyHex = ['#57d29a', '#b4d860', '#ecbf4f', '#ee8c4b', '#ec5d55'];
export const latencyColorHex = (v: number | null | undefined) => {
  const l = latencyLevel(v);
  return l < 0 ? '#5b5e6b' : latencyHex[l];
};

export function lossLevel(loss: number): 'ok' | 'warn' | 'bad' {
  if (loss <= 0) return 'ok';
  if (loss < 5) return 'warn';
  return 'bad';
}

export function bits(bps: number): string {
  if (bps < 1000) return `${Math.round(bps)} бит/с`;
  if (bps < 1e6) return `${(bps / 1e3).toFixed(bps < 1e4 ? 1 : 0)} Кбит/с`;
  if (bps < 1e9) return `${(bps / 1e6).toFixed(bps < 1e7 ? 1 : 0)} Мбит/с`;
  return `${(bps / 1e9).toFixed(1)} Гбит/с`;
}

export function linkSpeed(bps: number): string | null {
  if (!bps || bps <= 0) return null;
  if (bps >= 1e9) return `${+(bps / 1e9).toFixed(1)} Гбит/с`;
  if (bps >= 1e6) return `${Math.round(bps / 1e6)} Мбит/с`;
  return `${Math.round(bps / 1e3)} Кбит/с`;
}

export function bytes(n: number): string {
  if (n < 1024) return `${n} Б`;
  if (n < 1024 ** 2) return `${(n / 1024).toFixed(1)} КБ`;
  if (n < 1024 ** 3) return `${(n / 1024 ** 2).toFixed(1)} МБ`;
  return `${(n / 1024 ** 3).toFixed(2)} ГБ`;
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
  const base = d.toLocaleTimeString('ru-RU', { hour12: false });
  return withMs ? `${base}.${String(d.getMilliseconds()).padStart(3, '0')}` : base;
}

export function km(v: number): string {
  if (v < 10) return `${v.toFixed(1)} км`;
  return `${Math.round(v).toLocaleString('ru-RU')} км`;
}

export function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10;
  const m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
  return many;
}

const regionNames = (() => {
  try {
    return new Intl.DisplayNames(['ru'], { type: 'region' });
  } catch {
    return null;
  }
})();

export function countryName(cc: string | null | undefined): string | null {
  if (!cc) return null;
  try {
    return regionNames?.of(cc.toUpperCase()) ?? cc;
  } catch {
    return cc;
  }
}

export const statusText: Record<string, string> = {
  ok: 'ответ цели',
  'ttl-expired': 'TTL истёк',
  timeout: 'таймаут',
  'net-unreachable': 'сеть недоступна',
  'host-unreachable': 'узел недоступен',
  'proto-unreachable': 'протокол недоступен',
  'port-unreachable': 'порт недоступен',
  prohibited: 'запрещено фильтром',
  'packet-too-big': 'пакет больше MTU',
  'param-problem': 'ошибка параметров',
  'source-quench': 'source quench',
  'bad-route': 'неверный маршрут',
  'no-resources': 'нет ресурсов',
  error: 'ошибка',
  pending: 'ожидание',
  'tcp-open': 'TCP: порт открыт',
  'tcp-refused': 'TCP: отказ (RST)',
  'tcp-timeout': 'TCP: таймаут',
};
