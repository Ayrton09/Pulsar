import { QueryClient } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, session } from '../../test/fakeApi';

const friend: Schemas['UserResponse'] = {
  id: '0192f1a0-0000-7000-8000-000000000002',
  userName: 'friend',
  displayName: null,
  email: null,
  isDisabled: false,
  twoFactorEnabled: false,
  createdAt: '2026-10-01T12:00:00Z',
  lastLoginAt: null,
  roles: [],
  access: [],
  apiKeys: 0,
};
const operatorRole: Schemas['RoleSummary'] = {
  id: '0192f1a0-0000-7000-8000-0000000000c1',
  name: 'Operator',
  description: null,
  scope: 'server',
  isSystem: false,
  requiresTwoFactor: false,
  permissions: ['server.view', 'server.start'],
  assignedUsers: 0,
  createdAt: '2026-10-01T12:00:00Z',
  updatedAt: '2026-10-01T12:00:00Z',
};
const globalRole: Schemas['RoleSummary'] = {
  ...operatorRole,
  id: '0192f1a0-0000-7000-8000-0000000000c2',
  name: 'Auditor',
  scope: 'global',
};
const server = { id: '0192f1a0-0000-7000-8000-0000000000e1', name: 'Retakes', nodeName: 'Buenos Aires 1' };
const granted: Schemas['AccessReference'] = {
  scope: 'server',
  targetId: server.id,
  targetName: 'Retakes',
  role: { id: operatorRole.id, name: 'Operator' },
};

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
  render(<App router={router} queryClient={queryClient} />);
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('users', () => {
  it('gives a role on one server and takes it away', async () => {
    const give = vi.fn(() => json({ ...friend, access: [granted] }));
    const take = vi.fn(() => json(friend));
    fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/users': () => json([friend]),
      'GET /api/v1/roles': () => json([globalRole, operatorRole]),
      'GET /api/v1/servers': () => json([server]),
      'GET /api/v1/nodes': () => json([]),
      [`PUT /api/v1/users/${friend.id}/servers/${server.id}/roles/${operatorRole.id}`]: give,
      [`DELETE /api/v1/users/${friend.id}/servers/${server.id}/roles/${operatorRole.id}`]: take,
    });
    renderAt('/admin/users');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Edit' }));
    const access = await screen.findByRole('region', { name: 'Access to nodes and servers' });
    expect(within(access).getByText('No roles on single nodes or servers.')).toBeInTheDocument();
    await user.selectOptions(within(access).getByLabelText('Server'), 'Retakes (Buenos Aires 1)');
    const roles = within(access).getByLabelText('Role');
    expect(within(roles).queryByRole('option', { name: 'Auditor' })).not.toBeInTheDocument(); // global roles are given above
    await user.selectOptions(roles, 'Operator');
    await user.click(within(access).getByRole('button', { name: 'Give access' }));

    expect(give).toHaveBeenCalledOnce();
    expect(await within(access).findByText(/Operator on Retakes/)).toBeInTheDocument();
    await user.click(within(access).getByRole('button', { name: 'Remove' }));
    expect(take).toHaveBeenCalledOnce();
    expect(await within(access).findByText('No roles on single nodes or servers.')).toBeInTheDocument();
  });

  it('stops offering to turn off two-factor authentication once it is off', async () => {
    let current: Schemas['UserResponse'] = { ...friend, twoFactorEnabled: true };
    const reset = vi.fn(() => {
      current = { ...current, twoFactorEnabled: false };
      return new Response(null, { status: 204 });
    });
    fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/users': () => json([current]),
      'GET /api/v1/roles': () => json([]),
      'GET /api/v1/servers': () => json([]),
      'GET /api/v1/nodes': () => json([]),
      [`POST /api/v1/users/${friend.id}/two-factor/reset`]: reset,
    });
    vi.stubGlobal('confirm', () => true);
    renderAt('/admin/users');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit friend' });
    expect(within(dialog).getByText('Two-factor authentication is on.')).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Turn off two-factor' }));

    expect(await within(dialog).findByText('Two-factor authentication turned off.')).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Turn off two-factor' })).not.toBeInTheDocument();
    expect(within(dialog).queryByText('Two-factor authentication is on.')).not.toBeInTheDocument();
    expect(reset).toHaveBeenCalledOnce();
  });
});
