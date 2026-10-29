import { QueryClient } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, session } from '../../test/fakeApi';
import { browser } from './api';

const settingsManager = { ...adminUser, permissions: [...adminUser.permissions, 'settings.manage'] };

const options: Schemas['SsoOption'][] = [
  { kind: 'discord', name: 'Discord' },
  { kind: 'oidc', name: 'Company account' },
];

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
  vi.restoreAllMocks();
});

describe('sign-in providers', () => {
  it('offers the providers turned on and goes to the one chosen', async () => {
    const go = vi.spyOn(browser, 'go').mockImplementation(() => undefined);
    const started: string[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(null),
      'GET /api/v1/auth/sso': () => json(options),
      'POST /api/v1/auth/sso/oidc/login': () => {
        started.push('oidc');
        return json({ url: 'https://idp.example/authorize?state=x' });
      },
    });
    renderAt('/login');

    await userEvent.click(await screen.findByRole('button', { name: 'Sign in with Company account' }));
    expect(screen.getByRole('button', { name: 'Sign in with Discord' })).toBeInTheDocument();
    await waitFor(() => expect(go).toHaveBeenCalledWith('https://idp.example/authorize?state=x'));
    expect(started).toEqual(['oidc']);
  });

  it('says why a sign-in with a provider did not end in the panel', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(null),
      'GET /api/v1/auth/sso': () => json(options),
    });
    renderAt('/login?sso_error=not_linked');

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'No account of the panel is linked to that account. Sign in with your password and link it from your account page.',
    );
  });

  it('asks for the second step when a provider signed in a two-factor account', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(null),
      'GET /api/v1/auth/sso': () => json(options),
    });
    renderAt('/login?sso=two_factor');

    expect(await screen.findByRole('heading', { name: 'Two-factor authentication' })).toBeInTheDocument();
  });

  it('links an account with the password and unlinks one', async () => {
    const go = vi.spyOn(browser, 'go').mockImplementation(() => undefined);
    const calls: string[] = [];
    let links: Schemas['SsoLinkView'][] = [{ kind: 'discord', name: 'Ada' }];
    fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/auth/me': () => json(adminUser),
      'GET /api/v1/auth/sso': () => json(options),
      'GET /api/v1/account/sso': () => json(links),
      'GET /api/v1/account/api-keys': () => json([]),
      'GET /api/v1/auth/two-factor': () => json({ enabled: false, recoveryCodesLeft: 0, required: false }),
      'POST /api/v1/account/sso/oidc/link': async (request) => {
        calls.push(`link ${JSON.stringify(await request.json())}`);
        return json({ url: 'https://idp.example/authorize?link' });
      },
      'DELETE /api/v1/account/sso/discord': () => {
        calls.push('unlink');
        links = [];
        return new Response(null, { status: 204 });
      },
    });
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderAt('/account');

    const section = await screen.findByRole('list', { name: 'Linked sign-in accounts' });
    expect(within(section).getByText('Linked as Ada')).toBeInTheDocument();
    await userEvent.click(within(section).getByRole('button', { name: 'Unlink' }));
    await waitFor(() => expect(within(section).queryByText('Linked as Ada')).not.toBeInTheDocument());

    // Discord is unlinked now, so it offers a link too: the OpenID Connect one is the last.
    const [, linkOidc] = within(section).getAllByRole('button', { name: 'Link' });
    await userEvent.click(linkOidc as HTMLElement);
    const dialog = await screen.findByRole('dialog', { name: 'Link Company account' });
    const next = within(dialog).getByRole('button', { name: 'Continue to Company account' });
    expect(next).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText('Current password'), 'my password');
    await userEvent.click(next);
    await waitFor(() => expect(go).toHaveBeenCalledWith('https://idp.example/authorize?link'));
    expect(calls).toEqual(['unlink', 'link {"password":"my password","code":null}']);
  });

  it('saves a provider without ever showing its secret, and shows the redirect to register', async () => {
    let saved: unknown = null;
    const provider = (kind: Schemas['SsoKind']): Schemas['SsoProviderView'] => ({
      kind,
      enabled: false,
      clientId: '',
      hasSecret: false,
      authority: null,
      displayName: null,
      callbackPath: `/api/v1/auth/sso/${kind}/callback`,
      updatedAt: null,
    });
    fakeApi({
      'GET /api/v1/auth/session': session(settingsManager),
      'GET /api/v1/sso-providers': () => json([provider('discord'), provider('google'), provider('oidc')]),
      'PUT /api/v1/sso-providers/oidc': async (request) => {
        saved = await request.json();
        return json({
          ...provider('oidc'),
          enabled: true,
          clientId: 'pulsar',
          hasSecret: true,
          updatedAt: '2026-10-29T12:00:00Z',
        });
      },
    });
    renderAt('/admin/sign-in');

    const card = await screen.findByRole('form', { name: 'OpenID Connect' });
    expect(card).toHaveTextContent(`${window.location.origin}/api/v1/auth/sso/oidc/callback`);
    await userEvent.click(within(card).getByLabelText('On'));
    await userEvent.type(within(card).getByLabelText('Issuer'), 'https://auth.example.com/realms/games');
    await userEvent.type(within(card).getByLabelText('Button name'), 'Company account');
    await userEvent.type(within(card).getByLabelText('Client id'), 'pulsar');
    await userEvent.type(within(card).getByLabelText('Client secret'), 'the secret');
    await userEvent.click(within(card).getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(saved).toEqual({
        enabled: true,
        clientId: 'pulsar',
        clientSecret: 'the secret',
        authority: 'https://auth.example.com/realms/games',
        displayName: 'Company account',
      }),
    );
    // Saved: the card starts again from what was stored, and still says so.
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
  }, 20_000);
});
