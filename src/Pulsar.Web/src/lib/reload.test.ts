import { afterEach, describe, expect, it, vi } from 'vitest';
import { isStaleBuildError, reloadForNewBuild } from './reload';

afterEach(() => {
  sessionStorage.clear();
  vi.restoreAllMocks();
});

describe('reloading for a new build', () => {
  it.each([
    'Failed to fetch dynamically imported module: https://panel/assets/NodesPage-1a2b3c.js',
    'error loading dynamically imported module: https://panel/assets/NodesPage-1a2b3c.js',
    'Importing a module script failed.',
    'Unable to preload CSS for /assets/NodesPage-1a2b3c.css',
  ])('recognizes a page of a build the panel no longer serves: %s', (message) => {
    expect(isStaleBuildError(new TypeError(message))).toBe(true);
  });

  it('does not take other errors for one', () => {
    expect(isStaleBuildError(new TypeError("Cannot read properties of undefined (reading 'name')"))).toBe(false);
    expect(isStaleBuildError('Failed to fetch dynamically imported module')).toBe(false);
  });

  it('reloads once, not again and again when the new build fails too', () => {
    const reload = vi.fn();

    expect(reloadForNewBuild(reload)).toBe(true);
    expect(reloadForNewBuild(reload)).toBe(false);
    expect(reload).toHaveBeenCalledOnce();
  });

  it('reloads again later, after another update', () => {
    const reload = vi.fn();
    const now = vi.spyOn(Date, 'now').mockReturnValue(1_000_000);
    reloadForNewBuild(reload);

    now.mockReturnValue(1_000_000 + 10 * 60_000);

    expect(reloadForNewBuild(reload)).toBe(true);
    expect(reload).toHaveBeenCalledTimes(2);
  });

  it('does not reload when it cannot remember having done so', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('Storage is disabled.');
    });
    const reload = vi.fn();

    expect(reloadForNewBuild(reload)).toBe(false);
    expect(reload).not.toHaveBeenCalled();
  });
});
