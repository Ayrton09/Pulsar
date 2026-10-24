import { QueryClient } from '@tanstack/react-query';
import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import i18next from 'i18next';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { safeReturnTo } from '../features/auth/returnTo';
import { adminUser, fakeApi, json, problem, session } from '../test/fakeApi';
import { App } from './App';
import de from '../locales/de/common.json';
import { initI18n, resolveLanguage } from './i18n';
import { routes } from './routes';

// Korean's texts cannot be fetched (offline, or the panel was updated since this tab loaded it).
vi.mock('../locales/ko/common.json', () => {
  throw new TypeError('Failed to fetch dynamically imported module');
});

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<App router={router} queryClient={queryClient} />);
  return router;
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('resolveLanguage', () => {
  it.each([
    ['es-AR', 'es'],
    ['es', 'es'],
    ['EN-us', 'en'],
    ['fr-FR', 'fr'],
    ['de-CH', 'de'],
    ['it-IT', 'it'],
    ['ja-JP', 'ja'],
    ['ko-KR', 'ko'],
    // Portugal's and Taiwan's browsers get Brazil's Portuguese and Simplified Chinese: closer than English.
    ['pt-PT', 'pt'],
    ['zh-CN', 'zh'],
    ['zh-TW', 'zh'],
    ['nl-NL', 'en'],
    ['', 'en'],
    [undefined, 'en'],
  ])('maps %s to %s', (tag, expected) => {
    expect(resolveLanguage(tag)).toBe(expected);
  });
});

describe('safeReturnTo', () => {
  it.each([
    ['/admin/users', '/admin/users'],
    ['//evil.example', '/'],
    ['/\\evil.example', '/'],
    ['https://evil.example', '/'],
    // Browsers strip tabs and line breaks from a URL: "/<tab>/evil.example" becomes "//evil.example".
    ['/\t/evil.example', '/'],
    ['/\n/evil.example', '/'],
    ['/\r/evil.example', '/'],
    ['/\t\\evil.example', '/'],
    [null, '/'],
  ])('maps %s to %s', (value, expected) => {
    expect(safeReturnTo(value)).toBe(expected);
  });
});

describe('routing', () => {
  it('sends a fresh installation to the setup page', async () => {
    fakeApi({ 'GET /api/v1/auth/session': session(null, true) });

    const router = renderAt('/admin/users');

    expect(await screen.findByRole('heading', { name: 'Create the administrator' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/setup');
  });

  it('asks anonymous users to sign in and remembers where they were going', async () => {
    fakeApi({ 'GET /api/v1/auth/session': session(null) });

    const router = renderAt('/admin/audit');

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(router.state.location.search).toBe('?returnTo=%2Fadmin%2Faudit');
  });

  it('shows administrators every administration entry', async () => {
    fakeApi({ 'GET /api/v1/auth/session': session(adminUser) });

    renderAt('/');

    expect(await screen.findByText('Welcome, Ada Admin.')).toBeInTheDocument();
    const nav = screen.getByRole('navigation', { name: 'Main navigation' });
    for (const name of ['Users', 'Roles', 'Audit log']) {
      expect(nav).toContainElement(screen.getByRole('link', { name }));
    }
  });

  it('opens the navigation from the menu button on small screens and closes it after a choice', async () => {
    fakeApi({ 'GET /api/v1/auth/session': session(adminUser) });
    const router = renderAt('/');
    const user = userEvent.setup();

    const menu = await screen.findByRole('button', { name: 'Open menu' });
    expect(menu).toHaveAttribute('aria-expanded', 'false');
    await user.click(menu);

    const drawer = screen.getByRole('dialog', { name: 'Menu' });
    expect(menu).toHaveAttribute('aria-expanded', 'true');
    await user.click(within(drawer).getByRole('link', { name: 'Audit log' }));

    expect(router.state.location.pathname).toBe('/admin/audit');
    expect(screen.queryByRole('dialog', { name: 'Menu' })).not.toBeInTheDocument();
  });

  it('closes the menu panel when the screen grows wide enough for the sidebar', async () => {
    const listeners: ((event: MediaQueryListEvent) => void)[] = [];
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: (_: string, listener: (event: MediaQueryListEvent) => void) => listeners.push(listener),
      removeEventListener: () => {},
    }));
    fakeApi({ 'GET /api/v1/auth/session': session(adminUser) });
    renderAt('/');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Open menu' }));
    expect(screen.getByRole('dialog', { name: 'Menu' })).toBeInTheDocument();

    // A tablet rotated to landscape: the sidebar shows, and the panel must not keep the page locked behind it.
    act(() => listeners.forEach((listener) => listener({ matches: true } as MediaQueryListEvent)));

    expect(screen.queryByRole('dialog', { name: 'Menu' })).not.toBeInTheDocument();
  });

  it('hides pages and links the user has no permission for', async () => {
    fakeApi({ 'GET /api/v1/auth/session': session({ ...adminUser, permissions: [] }) });

    renderAt('/admin/users');

    expect(await screen.findByRole('heading', { name: 'Not available' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Users' })).not.toBeInTheDocument();
  });
});

describe('login', () => {
  it('shows the translated API error', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(null),
      'POST /api/v1/auth/login': () => problem(401, 'auth.invalid_credentials', 'Invalid user name or password.'),
    });
    renderAt('/login');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('User name'), 'admin');
    await user.type(screen.getByLabelText('Password'), 'wrong password');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid user name or password.');
  });

  it('validates required fields before calling the API', async () => {
    const api = fakeApi({ 'GET /api/v1/auth/session': session(null) });
    renderAt('/login');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Sign in' }));

    expect(await screen.findAllByText('Required.')).toHaveLength(2);
    expect(api).toHaveBeenCalledTimes(1); // only the session
  });

  it('goes back to the requested page after signing in', async () => {
    let signedIn = false;
    fakeApi({
      'GET /api/v1/auth/session': () =>
        json({ setupRequired: false, user: signedIn ? adminUser : null, insecureConnection: false }),
      'POST /api/v1/auth/login': () => {
        signedIn = true;
        return json(adminUser);
      },
      'GET /api/v1/audit': () => json({ entries: [], nextBefore: null }),
    });
    const router = renderAt('/login?returnTo=%2Fadmin%2Faudit');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('User name'), 'admin');
    await user.type(screen.getByLabelText('Password'), 'correct horse battery staple');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('heading', { name: 'Audit log' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/admin/audit');
  });
});

describe('account', () => {
  it('shows a translated error for a wrong current password', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/auth/two-factor': () => json({ enabled: false, required: false, recoveryCodesLeft: 0 }),
      'GET /api/v1/account/api-keys': () => json([]),
      'POST /api/v1/auth/password': () =>
        problem(400, 'auth.wrong_current_password', 'The current password is not correct.'),
    });
    renderAt('/account');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Current password'), 'not it');
    await user.type(screen.getByLabelText('New password'), 'a brand new password');
    await user.type(screen.getByLabelText('Confirm password'), 'a brand new password');
    await user.click(screen.getByRole('button', { name: 'Change password' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The current password is not correct.');
  });

  it('checks that both new passwords match before calling the API', async () => {
    const api = fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/auth/two-factor': () => json({ enabled: false, required: false, recoveryCodesLeft: 0 }),
    });
    renderAt('/account');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Current password'), 'current one');
    await user.type(screen.getByLabelText('New password'), 'a brand new password');
    await user.type(screen.getByLabelText('Confirm password'), 'a different password');
    await user.click(screen.getByRole('button', { name: 'Change password' }));

    expect(await screen.findByText('The passwords do not match.')).toBeInTheDocument();
    expect(api.mock.calls.map(([request]) => (request as Request).method)).not.toContain('POST');
  });

  it('changes the language of the interface and keeps it in this browser for the next visits', async () => {
    const api = fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/auth/two-factor': () => json({ enabled: false, required: false, recoveryCodesLeft: 0 }),
      'GET /api/v1/account/api-keys': () => json([]),
    });
    renderAt('/account');
    const user = userEvent.setup();

    try {
      const select = await screen.findByLabelText('Panel language');
      // Each language by its own name, whatever the interface's language.
      expect(within(select).getByRole('option', { name: '简体中文' })).toHaveAttribute('lang', 'zh');
      await user.selectOptions(select, 'de');

      const german = de.account;
      expect(await screen.findByRole('heading', { name: german.title })).toBeInTheDocument();
      expect(screen.getByLabelText(german.language.label)).toHaveValue('de');
      expect(document.documentElement.lang).toBe('de');
      expect(localStorage.getItem('pulsar.language')).toBe('de');
      // The choice is this browser's: nothing is sent to the panel.
      expect(api.mock.calls.map(([request]) => (request as Request).method)).not.toContain('PUT');

      // The next visit starts in it, whatever the browser's language.
      await initI18n('es-AR');
      expect(i18next.language).toBe('de');
    } finally {
      localStorage.removeItem('pulsar.language');
      await i18next.changeLanguage('en');
    }
  });

  it('says so, and changes nothing, when a language cannot be fetched', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/auth/two-factor': () => json({ enabled: false, required: false, recoveryCodesLeft: 0 }),
      'GET /api/v1/account/api-keys': () => json([]),
    });
    renderAt('/account');
    const user = userEvent.setup();

    const select = await screen.findByLabelText('Panel language');
    await user.selectOptions(select, 'ko');

    expect(await screen.findByRole('alert')).toHaveTextContent('That language could not be loaded.');
    expect(select).toHaveValue('en');
    expect(i18next.language).toBe('en');
    expect(localStorage.getItem('pulsar.language')).toBeNull();
  });
});
