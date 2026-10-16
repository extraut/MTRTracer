import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import './design/base.css';
import { App } from './App';
import { useStore } from './store';
import { applyTheme, savedTheme } from './theme';

// Apply the remembered theme before the first paint (settings arrive a moment later).
applyTheme(savedTheme());

/** Language and theme changes remount the app: every text and resolved colour is read fresh. */
function Root() {
  const key = useStore((s) => `${s.lang}|${s.resolvedTheme}`);
  return <App key={key} />;
}

// Block the browser's own shortcuts that make no sense inside a desktop app (reload, print, find).
window.addEventListener('keydown', (e) => {
  const k = e.key.toLowerCase();
  if (e.key === 'F5' || (e.ctrlKey && (k === 'r' || k === 'p' || k === 'f' || k === 'g' || k === 'u'))) e.preventDefault();
});
window.addEventListener('contextmenu', (e) => {
  const t = e.target as HTMLElement;
  if (!t.closest('input, textarea, .selectable')) e.preventDefault();
});

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Root />
  </StrictMode>,
);
