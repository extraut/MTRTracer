import { useEffect, useRef } from 'react';
import { latencyColorHex } from '../lib/format';

/** Per-hop history strip: one bar per probe, height = RTT, color = latency band, red notch = lost. */
export function Sparkline({ values, width, height, slots = 60, scaleMax }: { values: (number | null)[]; width: number; height: number; slots?: number; scaleMax?: number }) {
  const ref = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const c = ref.current;
    if (!c) return;
    const dpr = window.devicePixelRatio || 1;
    if (c.width !== Math.round(width * dpr)) {
      c.width = Math.round(width * dpr);
      c.height = Math.round(height * dpr);
    }
    const ctx = c.getContext('2d')!;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, height);
    const data = values.slice(-slots);
    const nums = data.filter((v): v is number => v != null);
    const max = scaleMax ?? Math.max(5, ...nums) * 1.08;
    const gap = 1;
    const bw = (width - (slots - 1) * gap) / slots;
    const offset = slots - data.length;
    ctx.fillStyle = 'rgba(120,124,140,0.10)';
    ctx.fillRect(0, height - 1, width, 1);
    data.forEach((v, i) => {
      const x = (offset + i) * (bw + gap);
      if (v == null) {
        ctx.fillStyle = 'rgba(236,93,85,0.55)';
        ctx.fillRect(x, 0, bw, height);
        return;
      }
      const h = Math.max(1.5, Math.min(1, v / max) * (height - 1));
      ctx.fillStyle = latencyColorHex(v);
      ctx.globalAlpha = i === data.length - 1 ? 1 : 0.78;
      ctx.fillRect(x, height - h, bw, h);
      ctx.globalAlpha = 1;
    });
  }, [values, width, height, slots, scaleMax]);
  return <canvas ref={ref} style={{ width, height, display: 'block' }} aria-hidden />;
}
