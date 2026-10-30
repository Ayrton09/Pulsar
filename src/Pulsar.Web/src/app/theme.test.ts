import { afterEach, describe, expect, it, vi } from 'vitest';
import { chooseTheme, currentTheme, initTheme, storedTheme } from './theme';

/** A system whose color scheme the test sets, telling the page when it changes. */
function fakeSystem(light: boolean) {
  const listeners: ((event: MediaQueryListEvent) => void)[] = [];
  const query = {
    get matches() {
      return light;
    },
    addEventListener: (_: string, listener: (event: MediaQueryListEvent) => void) => listeners.push(listener),
    removeEventListener: () => undefined,
  };
  vi.stubGlobal('matchMedia', () => query);
  return (next: boolean) => {
    light = next;
    listeners.forEach((listener) => listener({ matches: next } as MediaQueryListEvent));
  };
}

describe('theme', () => {
  afterEach(() => {
    localStorage.removeItem('pulsar.theme');
    delete document.documentElement.dataset.theme;
    vi.unstubAllGlobals();
  });

  it('is dark until another is chosen, whatever the system uses', () => {
    fakeSystem(true);
    initTheme();
    expect([storedTheme(), currentTheme(), document.documentElement.dataset.theme]).toEqual([
      'dark',
      'dark',
      undefined,
    ]);
  });

  it('keeps the choice in this browser for the next visits', () => {
    fakeSystem(false);
    chooseTheme('light');
    expect(currentTheme()).toBe('light');

    delete document.documentElement.dataset.theme;
    initTheme();
    expect(currentTheme()).toBe('light');

    chooseTheme('dark');
    expect(document.documentElement.dataset.theme).toBeUndefined();
  });

  it("follows the system's when that is the choice, also when the system changes", () => {
    const systemTurns = fakeSystem(false);
    chooseTheme('system');
    initTheme();
    expect(currentTheme()).toBe('dark');

    systemTurns(true);
    expect(currentTheme()).toBe('light');

    // Once another is chosen, the system is no longer followed.
    chooseTheme('dark');
    systemTurns(false);
    systemTurns(true);
    expect(currentTheme()).toBe('dark');
  });

  it('keeps the choice for this visit when the browser refuses storage', () => {
    fakeSystem(false);
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('Storage refused', 'SecurityError');
    });
    chooseTheme('light');

    expect([storedTheme(), currentTheme()]).toEqual(['light', 'light']);
    vi.restoreAllMocks();
    chooseTheme('dark');
  });

  it('ignores a stored value that is not a theme (the browser keeps what anyone put there)', () => {
    localStorage.setItem('pulsar.theme', 'neon');
    expect(storedTheme()).toBe('dark');
  });
});
