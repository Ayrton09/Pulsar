import { QueryClient } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, noUpdates, problem, session } from '../../test/fakeApi';

const hostId = '0192f1a0-0000-7000-8000-0000000000b1';
const otherId = '0192f1a0-0000-7000-8000-0000000000b2';
const manager = { ...adminUser, permissions: [...adminUser.permissions, 'settings.manage'] };
const node = (id: string, name: string, extra: Partial<Schemas['NodeUpdateView']>): Schemas['NodeUpdateView'] => ({
  nodeId: id,
  name,
  connected: true,
  agentVersion: '1.0.0',
  hostsPanel: false,
  canUpdate: true,
  updateAvailable: true,
  blocked: null,
  last: null,
  ...extra,
});
const available: Schemas['UpdatesView'] = {
  ...noUpdates,
  checkedAt: '2026-10-21T10:00:00Z',
  available: {
    version: '1.0.1',
    published: '2026-10-21T09:00:00Z',
    notes: 'Faster backups.\n<b>not markup</b>',
    newerThanPanel: true,
  },
  nodes: [node(hostId, 'Home PC', { hostsPanel: true }), node(otherId, 'Second PC', { blocked: 'update.panel_first' })],
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

describe('updates', () => {
  it('shows a newer release and its notes as text, with the panel first', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      'GET /api/v1/updates': () => json(available),
    });
    renderAt('/admin/updates');

    expect(await screen.findByRole('heading', { name: 'Pulsar 1.0.1 is available' })).toBeInTheDocument();
    // The notes are plain text: what looks like markup is shown as it is.
    expect(screen.getByText(/<b>not markup<\/b>/)).toBeInTheDocument();
    // Every page tells who can update.
    expect(screen.getByText('Pulsar 1.0.1 is available.')).toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'Nodes' });
    const host = within(table).getByRole('row', { name: /Home PC/ });
    const other = within(table).getByRole('row', { name: /Second PC/ });
    expect(within(host).getByRole('button', { name: 'Update the panel' })).toBeInTheDocument();
    expect(within(other).queryByRole('button')).not.toBeInTheDocument();
    expect(within(other).getByText("Update the panel's machine first.")).toBeInTheDocument();
  });

  it('updates a node with the password, and says what a node refused', async () => {
    let sent: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      'GET /api/v1/updates': () => json(available),
      'POST /api/v1/updates/apply': async (request) => {
        sent = await request.json();
        return json([{ nodeId: hostId, sent: false, errorCode: 'update.msi_managed', message: null }]);
      },
    });
    renderAt('/admin/updates');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Update the panel' }));
    const dialog = await screen.findByRole('dialog', { name: 'Update to Pulsar 1.0.1' });
    const confirm = within(dialog).getByRole('button', { name: 'Update' });
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByLabelText('Current password'), 'my password');
    await user.click(confirm);

    expect(await within(dialog).findByText(/Home PC: The agent MSI maintains this node/)).toBeInTheDocument();
    expect(sent).toEqual({ nodeIds: [hostId], password: 'my password', code: null });
  });

  it('says why a wrong password was refused, and asks for the code with two-factor authentication', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session({ ...manager, twoFactorEnabled: true }),
      'GET /api/v1/updates': () => json(available),
      'POST /api/v1/updates/apply': () => problem(400, 'auth.wrong_current_password'),
    });
    renderAt('/admin/updates');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Update the panel' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Current password'), 'wrong');
    const confirm = within(dialog).getByRole('button', { name: 'Update' });
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByLabelText('Code from the app'), '123456');
    await user.click(confirm);

    expect(await within(dialog).findByRole('alert')).toBeInTheDocument();
  });

  it('shows no banner and no page to those who cannot manage the settings', async () => {
    const calls = fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/users': () => json([]),
      'GET /api/v1/roles': () => json([]),
    });
    renderAt('/admin/users');

    expect(await screen.findByRole('heading', { name: 'Users' })).toBeInTheDocument();
    expect(screen.queryByText(/is available/)).not.toBeInTheDocument();
    expect(
      calls.mock.calls.some(([input]) =>
        String(input instanceof Request ? input.url : input).includes('/api/v1/updates'),
      ),
    ).toBe(false);
  });
});
