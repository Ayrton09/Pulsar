import { QueryClient } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { safeReturnTo } from '../features/auth/returnTo';
import { adminUser, fakeApi, json, problem, session } from '../test/fakeApi';
import { App } from './App';
import { initI18n, resolveLanguage } from './i18n';
import { routes } from './routes';

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
    ['fr-FR', 'en'],
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
});
