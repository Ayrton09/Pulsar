import { afterEach, describe, expect, it, vi } from 'vitest';
import { isStaleBuildError, panelAnswers, reloadForNewBuild } from './reload';

const answers = () => Promise.resolve(true);

afterEach(() => {
  sessionStorage.clear();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
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

  it('reloads once, not again and again when the new build fails too', async () => {
    const reload = vi.fn();

    expect(await reloadForNewBuild(reload, answers)).toBe('reloading');
    expect(await reloadForNewBuild(reload, answers)).toBe('reloaded');
    expect(reload).toHaveBeenCalledOnce();
  });

  it('reloads again later, after another update', async () => {
    const reload = vi.fn();
    const now = vi.spyOn(Date, 'now').mockReturnValue(1_000_000);
    await reloadForNewBuild(reload, answers);

    now.mockReturnValue(1_000_000 + 10 * 60_000);

    expect(await reloadForNewBuild(reload, answers)).toBe('reloading');
    expect(reload).toHaveBeenCalledTimes(2);
  });

  it('does not reload when it cannot remember having done so', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('Storage is disabled.');
    });
    const reload = vi.fn();

    expect(await reloadForNewBuild(reload, answers)).toBe('reloaded');
    expect(reload).not.toHaveBeenCalled();
  });

  it('does not reload while the panel does not answer, and reloads once it does', async () => {
    const reload = vi.fn();

    // Offline or restarting: a reload would show the browser's own error page, and lose the tab.
    expect(await reloadForNewBuild(reload, () => Promise.resolve(false))).toBe('unreachable');
    expect(reload).not.toHaveBeenCalled();

    expect(await reloadForNewBuild(reload, answers)).toBe('reloading');
    expect(reload).toHaveBeenCalledOnce();
  });
});

describe('whether the panel answers', () => {
  it('does not ask while the browser is offline', async () => {
    vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false);
    const fetch = vi.fn();
    vi.stubGlobal('fetch', fetch);

    expect(await panelAnswers()).toBe(false);
    expect(fetch).not.toHaveBeenCalled();
  });

  it('asks the panel, past any cache', async () => {
    const fetch = vi.fn(() => Promise.resolve(new Response('Healthy')));
    vi.stubGlobal('fetch', fetch);

    expect(await panelAnswers()).toBe(true);
    expect(fetch).toHaveBeenCalledWith('/health/live', { cache: 'no-store' });
  });

  it('is false when the panel is down or a proxy answers for it', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.reject(new TypeError('Failed to fetch'))),
    );
    expect(await panelAnswers()).toBe(false);

    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response('Bad Gateway', { status: 502 }))),
    );
    expect(await panelAnswers()).toBe(false);
  });
});
