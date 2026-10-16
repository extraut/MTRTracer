import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { create } from 'zustand';
import './contextmenu.css';

export type MenuItem =
  | { label: string; hint?: string; icon?: React.ReactNode; disabled?: boolean; onSelect: () => void }
  | { separator: true }
  | { title: string };

interface MenuState {
  x: number;
  y: number;
  items: MenuItem[] | null;
  open(e: React.MouseEvent, items: MenuItem[]): void;
  close(): void;
}

export const useContextMenu = create<MenuState>((set) => ({
  x: 0,
  y: 0,
  items: null,
  open(e, items) {
    e.preventDefault();
    e.stopPropagation();
    set({ x: e.clientX, y: e.clientY, items });
  },
  close() {
    set({ items: null });
  },
}));

/** Single host for all right-click menus; positioned at the cursor and kept inside the window. */
export function ContextMenuHost() {
  const { x, y, items, close } = useContextMenu();
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState({ x, y });
  const [active, setActive] = useState(-1);

  useLayoutEffect(() => {
    if (!items || !ref.current) return;
    const r = ref.current.getBoundingClientRect();
    setPos({ x: Math.min(x, window.innerWidth - r.width - 6), y: Math.min(y, window.innerHeight - r.height - 6) });
    setActive(-1);
  }, [items, x, y]);

  useEffect(() => {
    if (!items) return;
    const selectable = items.map((it, i) => ('onSelect' in it && !it.disabled ? i : -1)).filter((i) => i >= 0);
    const onDown = (e: MouseEvent) => ref.current && !ref.current.contains(e.target as Node) && close();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close();
      else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        setActive((a) => {
          const idx = selectable.indexOf(a);
          const next = e.key === 'ArrowDown' ? idx + 1 : idx - 1;
          return selectable[(next + selectable.length) % selectable.length] ?? -1;
        });
      } else if (e.key === 'Enter') {
        setActive((a) => {
          const it = items[a];
          if (it && 'onSelect' in it) {
            close();
            it.onSelect();
          }
          return a;
        });
      }
    };
    window.addEventListener('mousedown', onDown);
    window.addEventListener('keydown', onKey);
    window.addEventListener('blur', close);
    window.addEventListener('resize', close);
    return () => {
      window.removeEventListener('mousedown', onDown);
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('blur', close);
      window.removeEventListener('resize', close);
    };
  }, [items, close]);

  if (!items) return null;
  return (
    <div ref={ref} className="ctx" role="menu" style={{ left: pos.x, top: pos.y }} onContextMenu={(e) => e.preventDefault()}>
      {items.map((it, i) =>
        'separator' in it ? (
          <div key={i} className="ctx-sep" />
        ) : 'title' in it ? (
          <div key={i} className="ctx-title mono">
            {it.title}
          </div>
        ) : (
          <button
            key={i}
            role="menuitem"
            className={`ctx-item ${i === active ? 'on' : ''}`}
            disabled={it.disabled}
            onMouseEnter={() => setActive(i)}
            onClick={() => {
              close();
              it.onSelect();
            }}
          >
            <span className="ctx-icon">{it.icon}</span>
            <span>{it.label}</span>
            {it.hint && <span className="ctx-hint">{it.hint}</span>}
          </button>
        ),
      )}
    </div>
  );
}
