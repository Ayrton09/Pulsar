import { QueryClient } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, problem, session } from '../../test/fakeApi';

// The account page's own requests, which tests of something else answer with nothing.
const accountPage = {
  'GET /api/v1/auth/two-factor': () => json({ enabled: false, required: false, recoveryCodesLeft: 0 }),
  'GET /api/v1/account/api-keys': () => json([]),
};

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
  render(<App router={router} queryClient={queryClient} />);
  return router;
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('signing out', () => {
  it('signs out and goes to the sign-in page', async () => {
    let signedIn = true;
    fakeApi({
      ...accountPage,
      'GET /api/v1/auth/session': () =>
        json({ setupRequired: false, user: signedIn ? adminUser : null, insecureConnection: false }),
      'POST /api/v1/auth/logout': () => {
        signedIn = false;
        return new Response(null, { status: 204 });
      },
    });
    renderAt('/account');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Sign out' }));

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
  });

  it('says so, and stays signed in, when the panel refuses', async () => {
    const api = fakeApi({
      ...accountPage,
      'GET /api/v1/auth/session': session(adminUser),
      'POST /api/v1/auth/logout': () => problem(500, 'unexpected'),
    });
    renderAt('/account');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Sign out' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Sign in' })).not.toBeInTheDocument();
    // The session was not reloaded as if it had ended.
    expect(
      api.mock.calls.filter(([input]) =>
        String(input instanceof Request ? input.url : input).endsWith('/auth/session'),
      ),
    ).toHaveLength(1);
  });
});
