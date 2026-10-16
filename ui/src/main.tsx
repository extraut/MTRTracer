import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import './design/base.css';
import { App } from './App';

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
    <App />
  </StrictMode>,
);
