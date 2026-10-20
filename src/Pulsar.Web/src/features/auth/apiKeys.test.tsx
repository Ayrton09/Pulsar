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

const catalog: Schemas['PermissionResponse'][] = [
  { key: 'audit.view', grantableScopes: ['global'] },
  { key: 'user.manage', grantableScopes: ['global'] },
  { key: 'server.start', grantableScopes: ['global', 'node', 'server'] },
];

const value = 'pulsar_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcde';

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<App router={router} queryClient={queryClient} />);
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('API keys', () => {
  it('creates a key limited to some permissions, shows its value once and revokes it', async () => {
    let keys: Schemas['ApiKeyView'][] = [];
    let sent: unknown;
    let revoked: string | null = null;
    fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/auth/two-factor': () => json({ enabled: false, required: false, recoveryCodesLeft: 0 }),
      'GET /api/v1/permissions': () => json(catalog),
      'GET /api/v1/account/api-keys': () => json(keys),
      'POST /api/v1/account/api-keys': async (request) => {
        sent = await request.json();
        const key: Schemas['ApiKeyView'] = {
          id: '0192f1a0-0000-7000-8000-0000000000k1',
          name: 'Discord bot',
          prefix: value.slice(0, 13),
          permissions: ['audit.view'],
          createdAt: '2026-10-05T12:00:00Z',
          expiresAt: '2027-01-03T12:00:00Z',
          lastUsedAt: null,
        };
        keys = [key];
        return json({ key, value }, 201);
      },
      'DELETE /api/v1/account/api-keys/0192f1a0-0000-7000-8000-0000000000k1': (request) => {
        revoked = new URL(request.url).pathname;
        keys = [];
        return new Response(null, { status: 204 });
      },
    });
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderAt('/account');
    const user = userEvent.setup();

    expect(await screen.findByText('You have no API keys.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Create key' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Name'), 'Discord bot');
    await user.click(within(dialog).getByLabelText('Only some of my permissions'));
    // Only what the user has: server.start is in the catalog but not among their permissions.
    expect(within(dialog).queryByLabelText('Start')).not.toBeInTheDocument();
    await user.click(within(dialog).getByLabelText('View the audit log'));
    await user.click(within(dialog).getByRole('button', { name: 'Create key' }));

    expect(await within(dialog).findByLabelText('API key')).toHaveTextContent(value);
    expect(sent).toEqual({ name: 'Discord bot', permissions: ['audit.view'], expiresInDays: 90 });
    // A browser that refuses the clipboard (plain HTTP, no permission) says so: the value is shown only now.
    vi.spyOn(navigator.clipboard, 'writeText').mockRejectedValue(new Error('denied'));
    await user.click(within(dialog).getByRole('button', { name: 'Copy' }));
    expect(await within(dialog).findByRole('status')).toHaveTextContent('The browser did not allow copying.');
    await user.click(within(dialog).getByRole('button', { name: 'I copied it' }));

    const list = await screen.findByRole('list', { name: 'API keys' });
    expect(within(list).getByText('Discord bot')).toBeInTheDocument();
    expect(within(list).getByText(/1 permission/)).toBeInTheDocument();
    expect(screen.queryByText(value)).not.toBeInTheDocument();

    await user.click(within(list).getByRole('button', { name: 'Revoke' }));
    expect(await screen.findByText('You have no API keys.')).toBeInTheDocument();
    expect(revoked).not.toBeNull();
  });

  it('stays open while the key is being created, so its value is not lost', async () => {
    let finish: (() => void) | undefined;
    fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/auth/two-factor': () => json({ enabled: false, required: false, recoveryCodesLeft: 0 }),
      'GET /api/v1/permissions': () => json(catalog),
      'GET /api/v1/account/api-keys': () => json([]),
      'POST /api/v1/account/api-keys': () =>
        new Promise<Response>((resolve) => {
          finish = () =>
            resolve(
              json(
                {
                  key: {
                    id: '0192f1a0-0000-7000-8000-0000000000k1',
                    name: 'Bot',
                    prefix: value.slice(0, 13),
                    permissions: null,
                    createdAt: '2026-10-05T12:00:00Z',
                    expiresAt: null,
                    lastUsedAt: null,
                  },
                  value,
                },
                201,
              ),
            );
        }),
    });
    renderAt('/account');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Create key' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Name'), 'Bot');
    await user.click(within(dialog).getByRole('button', { name: 'Create key' }));
    await user.keyboard('{Escape}');

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Cancel' })).toBeDisabled();
    finish?.();
    expect(await within(dialog).findByLabelText('API key')).toHaveTextContent(value);
  });

  it('lists a key created while the list was still loading', async () => {
    // The page's first request read the keys before the new one existed, and its answer arrives after the creation.
    let answerFirst: (response: Response) => void = () => {};
    const first = new Promise<Response>((resolve) => (answerFirst = resolve));
    const bot: Schemas['ApiKeyView'] = {
      id: '0192f1a0-0000-7000-8000-0000000000k1',
      name: 'Bot',
      prefix: value.slice(0, 13),
      permissions: null,
      createdAt: '2026-10-05T12:00:00Z',
      expiresAt: null,
      lastUsedAt: null,
    };
    let keys: Schemas['ApiKeyView'][] = [];
    let lists = 0;
    const create = vi.fn(() => {
      keys = [bot];
      return json({ key: bot, value }, 201);
    });
    fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/auth/two-factor': () => json({ enabled: false, required: false, recoveryCodesLeft: 0 }),
      'GET /api/v1/permissions': () => json(catalog),
      'GET /api/v1/account/api-keys': () => (lists++ === 0 ? first : json(keys)),
      'POST /api/v1/account/api-keys': create,
    });
    renderAt('/account');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Create key' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Name'), 'Bot');
    await user.click(within(dialog).getByRole('button', { name: 'Create key' }));
    await vi.waitFor(() => expect(create).toHaveBeenCalledOnce());
    answerFirst(json([])); // late, and without the new key

    expect(await within(dialog).findByLabelText('API key')).toHaveTextContent(value);
    await user.click(within(dialog).getByRole('button', { name: 'I copied it' }));
    // A shorter wait than the default: the assertion fails with its message before the test itself times out.
    await waitFor(() => expect(screen.queryByText('Bot')).toBeInTheDocument(), { timeout: 2000 });
    expect(screen.queryByText('You have no API keys.')).not.toBeInTheDocument();
  });
});
