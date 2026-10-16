import type { Lang } from './bridge/protocol';

/**
 * Interface language. Both variants sit next to each other at the call site: L('Старт', 'Start').
 * Switching the language remounts the app (see main.tsx), so module-level reads are always current.
 */
let current: Lang = guess();

function guess(): Lang {
  try {
    const saved = localStorage.getItem('mtr.lang');
    if (saved === 'ru' || saved === 'en') return saved;
  } catch {
    /* storage unavailable */
  }
  return navigator.language?.toLowerCase().startsWith('ru') ? 'ru' : 'en';
}

export const lang = () => current;
export const isEn = () => current === 'en';

export function setLang(l: Lang) {
  current = l;
  document.documentElement.lang = l;
  try {
    localStorage.setItem('mtr.lang', l);
  } catch {
    /* storage unavailable */
  }
}

export const L = (ru: string, en: string) => (current === 'en' ? en : ru);

/** Locale for dates and numbers. */
export const locale = () => (current === 'en' ? 'en-GB' : 'ru-RU');

/**
 * Plural form. Russian: [один, несколько (2-4), много]; English: [one, other].
 * Returns the word only; callers put the number in front.
 */
export function plural(n: number, ru: [string, string, string], en: [string, string]) {
  if (current === 'en') return Math.abs(n) === 1 ? en[0] : en[1];
  const a = Math.abs(n) % 100;
  const b = a % 10;
  if (a > 10 && a < 20) return ru[2];
  if (b > 1 && b < 5) return ru[1];
  if (b === 1) return ru[0];
  return ru[2];
}

document.documentElement.lang = current;
