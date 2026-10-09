import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
import { afterEach, vi } from 'vitest';

// The live channel and the terminal have stand-ins the tests drive (test/fakeSignalR.ts, test/fakeXterm.ts).
vi.mock('@microsoft/signalr', () => import('./fakeSignalR'));
vi.mock('@xterm/xterm', () => import('./fakeXterm'));
vi.mock('@xterm/addon-fit', () => import('./fakeXterm'));
vi.mock('@xterm/addon-search', () => import('./fakeXterm'));
vi.mock('@xterm/addon-web-links', () => import('./fakeXterm'));

// jsdom has no layout, so no ResizeObserver (assigned, not stubbed: tests unstub their own globals).
globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
};

// A page's first render loads its lazy route and its translations: on a busy machine (the .NET suite running alongside)
// that can take longer than findBy's default second.
configure({ asyncUtilTimeout: 5000 });

afterEach(() => {
  cleanup();
});
