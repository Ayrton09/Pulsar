import { QueryClient } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, problem, session } from '../../test/fakeApi';

const nodeId = '0192f1a0-0000-7000-8000-0000000000a1';
const templateId = '0192f1a0-0000-7000-8000-0000000000f2';
const friendId = '0192f1a0-0000-7000-8000-000000000002';

const player: Schemas['CurrentUserResponse'] = {
  ...adminUser,
  userName: 'player',
  permissions: [],
  hasQuota: true,
};

const quota: Schemas['QuotaView'] = {
  maxServers: 2,
  maxMemoryMb: 4096,
  maxCpuPercent: 150,
  used: { servers: 1, memoryMb: 1024, cpuPercent: 50 },
  nodes: [{ id: nodeId, name: 'Buenos Aires 1', connected: true }],
  games: [
    {
      templateId,
      key: 'scpsl',
      name: 'SCP: Secret Laboratory',
      version: '1.3.0',
      minMemoryMb: 2048,
      variables: [
        {
          key: 'eula',
          label: 'Accept the EULA',
          type: 'enum',
          default: null,
          required: true,
          secret: false,
          userEditable: true,
          pattern: null,
          maxLength: null,
          min: null,
          max: null,
          options: [{ value: 'true', label: 'I accept' }],
          description: null,
        },
      ],
    },
  ],
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
  vi.restoreAllMocks();
});

describe('a quota', () => {
  it("lets its user create a server of their own within what is left of it, the game's needed values included", async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(player),
      'GET /api/v1/account/quota': () => json(quota),
      'POST /api/v1/account/servers': async (request) => {
        body = await request.json();
        return problem(409, 'quota.memory');
      },
    });
    renderAt('/servers');
    const user = userEvent.setup();

    // What is used of it, on the page.
    expect(await screen.findByText('1 of 2')).toBeInTheDocument();
    expect(screen.getByText('1024 of 4096')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'New server' }));
    const dialog = await screen.findByRole('dialog', { name: 'New server' });

    // Over what is left: refused before sending.
    await user.selectOptions(within(dialog).getByLabelText('Game'), templateId);
    expect(within(dialog).getByText('The game asks for 2048 MB.')).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText('Name'), 'Site 19');
    const memory = within(dialog).getByLabelText('Memory (MB)');
    await user.clear(memory);
    await user.type(memory, '4000');
    await user.click(within(dialog).getByRole('button', { name: 'Create server' }));
    expect(within(dialog).getByText('Between 128 and 3072 MB: what your quota has left.')).toBeInTheDocument();
    expect(body).toBeUndefined();

    await user.clear(memory);
    await user.type(memory, '3072');
    await user.selectOptions(within(dialog).getByLabelText(/Accept the EULA/), 'true');
    await user.click(within(dialog).getByRole('button', { name: 'Create server' }));

    await vi.waitFor(() =>
      expect(body).toEqual({
        nodeId,
        templateId,
        name: 'Site 19',
        memoryMb: 3072,
        cpuPercent: 100,
        variables: { eula: 'true' },
      }),
    );
    expect(await within(dialog).findByText('That much memory would go over your quota.')).toBeInTheDocument();
  });

  it('is offered beside creating servers elsewhere', async () => {
    // server.create on another node does not hide it: its nodes are only its own.
    fakeApi({
      'GET /api/v1/auth/session': session({ ...player, permissions: ['server.create'] }),
      'GET /api/v1/account/quota': () => json(quota),
    });
    renderAt('/servers');

    expect(await screen.findByRole('button', { name: 'New server in my quota' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create server' })).toBeInTheDocument();
    expect(screen.getByText('1 of 2')).toBeInTheDocument();
  });

  it('that cannot be read is said so, and read again on request', async () => {
    let calls = 0;
    fakeApi({
      'GET /api/v1/auth/session': session(player),
      'GET /api/v1/account/quota': () => (++calls === 1 ? problem(500, 'internal') : json(quota)),
    });
    renderAt('/servers');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Try again' }));

    expect(await screen.findByText('1 of 2')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'New server' })).toBeInTheDocument();
  });

  it('offers nothing to create to a user without one', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session({ ...player, hasQuota: false }),
    });
    renderAt('/servers');

    expect(await screen.findByRole('alert')).toHaveTextContent('Not available');
    expect(screen.queryByRole('button', { name: 'New server' })).not.toBeInTheDocument();
  });

  it('is given to a user, on the nodes the manager may create servers on', async () => {
    const manager = { ...adminUser, permissions: [...adminUser.permissions, 'node.view', 'server.create'] };
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      'GET /api/v1/users': () =>
        json([
          {
            id: friendId,
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
            sshKeys: 0,
          },
        ]),
      'GET /api/v1/roles': () => json([]),
      'GET /api/v1/servers': () => json([]),
      'GET /api/v1/nodes': () =>
        json([
          { id: nodeId, name: 'Buenos Aires 1', status: 'active', connected: true, createdAt: '2026-10-01T12:00:00Z' },
        ]),
      [`PUT /api/v1/users/${friendId}/quota`]: async (request) => {
        body = await request.json();
        return json({
          maxServers: 3,
          maxMemoryMb: 4096,
          maxCpuPercent: 100,
          nodeIds: [nodeId],
          used: { servers: 0, memoryMb: 0, cpuPercent: 0 },
        });
      },
    });
    renderAt('/admin/users');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit friend' });
    const servers = await within(dialog).findByLabelText('Servers');
    await user.clear(servers);
    await user.type(servers, '3');
    await user.click(within(dialog).getByLabelText('Buenos Aires 1'));
    await user.click(within(dialog).getByRole('button', { name: 'Give quota' }));

    await vi.waitFor(() =>
      expect(body).toEqual({ maxServers: 3, maxMemoryMb: 4096, maxCpuPercent: 100, nodeIds: [nodeId] }),
    );
    expect(await within(dialog).findByRole('button', { name: 'Remove quota' })).toBeInTheDocument();
  });
});
