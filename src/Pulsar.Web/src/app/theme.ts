import { useSyncExternalStore } from 'react';

/** What a user chose in My account: a theme, or the one the system (Windows, the phone) uses. */
export type ThemeChoice = 'dark' | 'light' | 'system';

/** The theme the page is drawn in. */
export type Theme = 'dark' | 'light';

export const themeChoices: readonly ThemeChoice[] = ['dark', 'light', 'system'];

/** Where this browser keeps the theme chosen in My account (public/theme.js reads it before the page is drawn). */
const storageKey = 'pulsar.theme';
const systemLight = '(prefers-color-scheme: light)';

/** The browser's own bar, in each theme's background. */
const barColors: Record<Theme, string> = { dark: '#0b0d12', light: '#f4f5f9' };

const listeners = new Set<() => void>();

/** The theme chosen in this browser; dark until one is. */
export function storedTheme(): ThemeChoice {
  try {
    const stored = localStorage.getItem(storageKey);
    return themeChoices.find((choice) => choice === stored) ?? 'dark';
  } catch {
    return 'dark'; // storage refused (a private window, site data blocked): the default
  }
}

export function resolveTheme(choice: ThemeChoice): Theme {
  if (choice !== 'system') {
    return choice;
  }
  return typeof window.matchMedia === 'function' && window.matchMedia(systemLight).matches ? 'light' : 'dark';
}

/** Draws the page in the chosen theme: the tokens of index.css follow the root's data-theme. */
function apply(choice: ThemeChoice) {
  const theme = resolveTheme(choice);
  const root = document.documentElement;
  if (theme === 'light') {
    root.dataset.theme = 'light';
  } else {
    delete root.dataset.theme;
  }
  document.querySelector('meta[name="color-scheme"]')?.setAttribute('content', theme);
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', barColors[theme]);
  listeners.forEach((listener) => listener());
}

/** Applies the stored choice and, for "system", follows the system when it changes (a phone's evening switch). */
export function initTheme() {
  apply(storedTheme());
  if (typeof window.matchMedia === 'function') {
    window.matchMedia(systemLight).addEventListener('change', () => {
      if (storedTheme() === 'system') {
        apply('system');
      }
    });
  }
}

/** Changes the theme now and keeps the choice in this browser, for the next visits. */
export function chooseTheme(choice: ThemeChoice) {
  try {
    localStorage.setItem(storageKey, choice);
  } catch {
    // Not kept: this visit only.
  }
  apply(choice);
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** The theme the page is drawn in now. */
export function currentTheme(): Theme {
  return document.documentElement.dataset.theme === 'light' ? 'light' : 'dark';
}

/** The choice and the theme drawn, kept up to date. */
export function useTheme(): { choice: ThemeChoice; theme: Theme } {
  const theme = useSyncExternalStore(subscribe, currentTheme);
  const choice = useSyncExternalStore(subscribe, storedTheme);
  return { choice, theme };
}
