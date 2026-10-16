import { latencyHex, latencyMuted } from './lib/format';

/** Colour themes. "system" follows the OS light/dark setting. */
export const themes = ['system', 'dark', 'light', 'mocha', 'nord'] as const;
export type ThemeId = (typeof themes)[number];
export type ResolvedTheme = Exclude<ThemeId, 'system'>;

/**
 * Resolved colours for consumers that cannot use CSS variables (MapLibre paint, canvas, uPlot).
 * Filled from the active theme's CSS tokens by applyTheme(); the app remounts after a theme change.
 */
export const palette = {
  ocean: '#111217',
  land: '#1d1f26',
  border: '#2d303a',
  accent: '#9b9cf0',
  server: '#c6c7ff',
  lost: '#ec5d55',
  gap: '#6b6f80',
  muted: '#5b5e6b',
  axis: '#7d8090',
  grid: 'rgba(120,124,140,0.12)',
  comet: '238,238,255',
  ring: '230,230,255',
  lostRgb: '236,93,85',
  dark: true,
};

let resolved: ResolvedTheme = 'dark';
export const resolvedTheme = () => resolved;

const probe = (() => {
  try {
    const c = document.createElement('canvas');
    c.width = c.height = 1;
    return c.getContext('2d', { willReadFrequently: true });
  } catch {
    return null;
  }
})();

/** Any CSS colour (oklch, color-mix…) to [r, g, b, a] through a 1-pixel canvas. */
function rgba(css: string): [number, number, number, number] | null {
  if (!probe || !css.trim()) return null;
  probe.clearRect(0, 0, 1, 1);
  probe.fillStyle = '#000';
  probe.fillStyle = css.trim();
  probe.fillRect(0, 0, 1, 1);
  const d = probe.getImageData(0, 0, 1, 1).data;
  return [d[0], d[1], d[2], d[3] / 255];
}

const hex = (c: [number, number, number, number] | null, fallback: string) =>
  c ? '#' + [c[0], c[1], c[2]].map((v) => v.toString(16).padStart(2, '0')).join('') : fallback;

export function systemPrefersLight() {
  try {
    return window.matchMedia('(prefers-color-scheme: light)').matches;
  } catch {
    return false;
  }
}

export function resolveTheme(id: ThemeId): ResolvedTheme {
  return id === 'system' ? (systemPrefersLight() ? 'light' : 'dark') : id;
}

export function applyTheme(id: ThemeId) {
  resolved = resolveTheme(id);
  const root = document.documentElement;
  root.dataset.theme = resolved;
  try {
    localStorage.setItem('mtr.theme', id);
  } catch {
    /* storage unavailable */
  }
  const cs = getComputedStyle(root);
  const v = (name: string) => cs.getPropertyValue(name);
  const col = (name: string, fallback: string) => hex(rgba(v(name)), fallback);
  palette.ocean = col('--map-ocean', palette.ocean);
  palette.land = col('--map-land', palette.land);
  palette.border = col('--map-border', palette.border);
  palette.accent = col('--accent', palette.accent);
  palette.server = col('--map-server', palette.server);
  palette.lost = col('--lat-4', palette.lost);
  palette.gap = col('--text-4', palette.gap);
  palette.muted = col('--text-4', palette.muted);
  palette.axis = col('--text-3', palette.axis);
  const g = rgba(v('--line'));
  palette.grid = g ? `rgba(${g[0]},${g[1]},${g[2]},0.55)` : palette.grid;
  const comet = rgba(v('--map-comet'));
  if (comet) palette.comet = `${comet[0]},${comet[1]},${comet[2]}`;
  palette.ring = palette.comet;
  const lost = rgba(v('--lat-4'));
  if (lost) palette.lostRgb = `${lost[0]},${lost[1]},${lost[2]}`;
  palette.dark = resolved !== 'light';
  const lat = [0, 1, 2, 3, 4].map((i) => col(`--lat-${i}`, latencyHex[i]));
  latencyHex.splice(0, lat.length, ...lat);
  latencyMuted.value = palette.muted;
}

export function savedTheme(): ThemeId {
  try {
    const t = localStorage.getItem('mtr.theme') as ThemeId | null;
    if (t && (themes as readonly string[]).includes(t)) return t;
  } catch {
    /* storage unavailable */
  }
  return 'dark';
}
