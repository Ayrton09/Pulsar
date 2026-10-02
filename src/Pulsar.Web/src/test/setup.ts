import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
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

afterEach(() => {
  cleanup();
});
