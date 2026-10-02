import { QueryClient } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { parseTimeSpan } from '../../lib/format';
import { adminUser, fakeApi, json, session } from '../../test/fakeApi';
import { enrollCommand, nodeState } from './nodeStatus';

const operator = {
  ...adminUser,
  permissions: [...adminUser.permissions, 'node.view', 'node.manage', 'node.credentials.manage'],
};
const viewer = { ...adminUser, permissions: ['node.view'] };

const online: Schemas['NodeSummary'] = {
  id: '0192f1a0-0000-7000-8000-0000000000a1',
  name: 'Buenos Aires 1',
  description: 'Main box',
  publicHost: 'play.example.com',
  status: 'active',
  connected: true,
  lastSeenAt: '2026-10-01T12:00:00Z',
  inventory: {
    hostname: 'BA-GAMES-01',
    osDescription: 'Windows Server 2025 Datacenter',
    osBuild: '26100.4061',
    cpuModel: 'AMD Ryzen 9 7950X',
    logicalCores: 32,
    memoryTotalBytes: 64 * 1024 ** 3,
    serversRoot: 'D:\\PulsarServers',
    agentVersion: '0.1.0',
    protocolVersion: 1,
  },
  metrics: {
    at: '2026-10-01T12:00:00Z',
    cpuPercent: 12.5,
    memoryUsedBytes: 16 * 1024 ** 3,
    memoryTotalBytes: 64 * 1024 ** 3,
    uptime: '3.04:05:06.1230000',
  },
  createdAt: '2026-09-01T12:00:00Z',
  panelKeyTrust: 'current',
};
const offline = { ...online, id: '0192f1a0-0000-7000-8000-0000000000a2', name: 'Backup', connected: false };
const pending: Schemas['NodeSummary'] = {
  ...online,
  id: '0192f1a0-0000-7000-8000-0000000000a3',
  name: 'New box',
  status: 'pendingEnrollment',
  connected: false,
  lastSeenAt: null,
  inventory: null,
  metrics: null,
};
const credential: Schemas['NodeCredentialView'] = {
  id: '0192f1a0-0000-7000-8000-0000000000c1',
  fingerprint: 'sha256:node-key-fingerprint',
  keyStorage: 'tpm',
  status: 'active',
  createdAt: '2026-09-01T12:00:00Z',
  lastUsedAt: '2026-10-01T12:00:00Z',
  revokedAt: null,
  revokeReason: null,
  supersededAt: null,
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

describe('node helpers', () => {
  it.each([
    [{ status: 'active', connected: true }, 'online'],
    [{ status: 'active', connected: false }, 'offline'],
    [{ status: 'pendingEnrollment', connected: false }, 'pending'],
    [{ status: 'disabled', connected: true }, 'disabled'],
  ] as const)('maps %o to %s', (node, state) => {
    expect(nodeState(node)).toBe(state);
  });

  it('builds a PowerShell command that runs the agent from the current folder', () => {
    expect(enrollCommand('https://panel.example.com', 'tok', 'sha256:abc')).toBe(
      '.\\PulsarAgent.exe enroll --panel https://panel.example.com --token tok --panel-key sha256:abc',
    );
  });

  it.each([
    ['00:07:30', 450],
    ['3.04:05:06.1230000', 3 * 86_400 + 4 * 3_600 + 5 * 60 + 6],
    ['nonsense', 0],
  ])('parses the TimeSpan %s', (value, seconds) => {
    expect(parseTimeSpan(value)).toBe(seconds);
  });
});

describe('nodes page', () => {
  it('lists nodes with their state and usage', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/nodes': () => json([online, offline, pending]),
    });

    renderAt('/nodes');

    const table = await screen.findByRole('table', { name: 'Nodes' });
    const rows = within(table).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('Buenos Aires 1');
    expect(rows[1]).toHaveTextContent('Online');
    expect(rows[1]).toHaveTextContent('12.5 % CPU');
    expect(rows[2]).toHaveTextContent('Offline');
    expect(rows[3]).toHaveTextContent('Awaiting enrollment');
    expect(screen.getByRole('link', { name: 'Nodes' })).toBeInTheDocument();
  });

  it('creates a node and shows its one-time enrollment command', async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/nodes': () => json([]),
      'POST /api/v1/nodes': async (request) => {
        body = await request.json();
        return json(
          {
            node: { ...pending, name: 'Córdoba 1' },
            enrollment: {
              token: 'one-time-token',
              expiresAt: '2026-10-01T12:30:00Z',
              panelKeyFingerprint: 'sha256:panel-key',
            },
          },
          201,
        );
      },
    });
    renderAt('/nodes');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Add node' }));
    await user.type(screen.getByLabelText('Name'), 'Córdoba 1');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    const command = await screen.findByLabelText('Enrollment command');
    expect(command).toHaveTextContent(
      `.\\PulsarAgent.exe enroll --panel ${window.location.origin} --token one-time-token --panel-key sha256:panel-key`,
    );
    expect(screen.getByRole('heading', { name: 'Enroll Córdoba 1' })).toBeInTheDocument();
    expect(body).toEqual({ name: 'Córdoba 1', description: null, publicHost: null });
  });

  it('does not offer to add nodes to users who can only view them', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      'GET /api/v1/nodes': () => json([online]),
    });

    renderAt('/nodes');

    expect(await screen.findByRole('table', { name: 'Nodes' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add node' })).not.toBeInTheDocument();
  });
});

describe('node page', () => {
  it('shows the inventory, live usage and credentials', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/nodes/${online.id}`]: () => json({ node: online, credentials: [credential] }),
    });

    renderAt(`/nodes/${online.id}`);

    expect(await screen.findByRole('heading', { name: 'Buenos Aires 1' })).toBeInTheDocument();
    expect(screen.getByText('BA-GAMES-01')).toBeInTheDocument();
    expect(screen.getByText('AMD Ryzen 9 7950X · 32 logical cores')).toBeInTheDocument();
    expect(screen.getByRole('meter', { name: 'CPU' })).toHaveAttribute('aria-valuenow', '13');
    expect(screen.getByText('3 d 4 h')).toBeInTheDocument();
    expect(screen.getByText('sha256:node-key-fingerprint')).toBeInTheDocument();
    expect(screen.getByText('Kept in the TPM')).toBeInTheDocument();
  });

  it('pings the agent and reports the round trip', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/nodes/${online.id}`]: () => json({ node: online, credentials: [credential] }),
      [`POST /api/v1/nodes/${online.id}/ping`]: () => json({ status: 'accepted', roundTripMilliseconds: 41.7 }),
    });
    renderAt(`/nodes/${online.id}`);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Test connection' }));

    expect(await screen.findByText('Answered in 42 ms')).toBeInTheDocument();
  });

  it('revokes a credential with a reason', async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/nodes/${online.id}`]: () => json({ node: online, credentials: [credential] }),
      [`POST /api/v1/nodes/${online.id}/credentials/${credential.id}/revoke`]: async (request) => {
        body = await request.json();
        return new Response(null, { status: 204 });
      },
    });
    renderAt(`/nodes/${online.id}`);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Revoke' }));
    const dialog = await screen.findByRole('dialog', { name: 'Revoke this credential?' });
    await user.type(within(dialog).getByLabelText('Reason (optional)'), 'Machine stolen');
    await user.click(within(dialog).getByRole('button', { name: 'Revoke' }));

    await vi.waitFor(() => expect(body).toEqual({ reason: 'Machine stolen' }));
  });

  it('shows viewers the node without administration actions', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      [`GET /api/v1/nodes/${online.id}`]: () => json({ node: online, credentials: [credential] }),
    });

    renderAt(`/nodes/${online.id}`);

    expect(await screen.findByRole('heading', { name: 'Buenos Aires 1' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Test connection' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Revoke' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Administration' })).not.toBeInTheDocument();
  });
});

describe('key rotation', () => {
  it('asks a connected agent to rotate its key', async () => {
    let asked = false;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/nodes/${online.id}`]: () => json({ node: online, credentials: [credential] }),
      [`POST /api/v1/nodes/${online.id}/credentials/rotate`]: () => {
        asked = true;
        return json({ status: 'accepted', errorCode: null });
      },
    });
    renderAt(`/nodes/${online.id}`);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Rotate key' }));

    expect(await screen.findByText(/The agent is replacing its key/)).toBeInTheDocument();
    expect(asked).toBe(true);
  });

  it('flags nodes that only trust a retiring panel key', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/nodes/${online.id}`]: () =>
        json({ node: { ...online, panelKeyTrust: 'outdated' }, credentials: [credential] }),
    });

    renderAt(`/nodes/${online.id}`);

    expect(await screen.findByText('Old panel key')).toBeInTheDocument();
  });

  it('lets settings managers rotate the panel key and then retire the old one', async () => {
    const admin = { ...operator, permissions: [...operator.permissions, 'settings.manage'] };
    const key = (keyId: string, status: Schemas['PanelSigningKeyStatus'], nodesTrusting: number) => ({
      keyId,
      status,
      createdAt: '2026-09-01T12:00:00Z',
      retiredAt: null,
      nodesTrusting,
    });
    const rotated = {
      keys: [key('sha256:new', 'active', 1), key('sha256:old', 'retiring', 2)],
      nodes: 2,
      nodesOutdated: 1,
      nodesUnknown: 0,
    };
    let retiredKey = '';
    fakeApi({
      'GET /api/v1/auth/session': session(admin),
      'GET /api/v1/nodes': () => json([online]),
      'GET /api/v1/panel-keys': () =>
        json({ keys: [key('sha256:old', 'active', 2)], nodes: 2, nodesOutdated: 0, nodesUnknown: 0 }),
      'POST /api/v1/panel-keys/rotate': () => json(rotated),
      // openapi-fetch encodes the ':' of the key id.
      'POST /api/v1/panel-keys/sha256%3Aold/retire': (request) => {
        retiredKey = new URL(request.url).pathname;
        return json({
          ...rotated,
          keys: [key('sha256:new', 'active', 2), { ...key('sha256:old', 'retired', 0) }],
          nodesOutdated: 0,
        });
      },
    });
    vi.stubGlobal('confirm', () => true);
    renderAt('/nodes');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Rotate panel key' }));

    expect(await screen.findByText('Being retired')).toBeInTheDocument();
    expect(screen.getByText(/1 node\(s\) still trust only the old key/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Retire old key' }));
    await vi.waitFor(() => expect(retiredKey).toContain('/retire'));
    expect(await screen.findByRole('button', { name: 'Rotate panel key' })).toBeInTheDocument();
  });
});
