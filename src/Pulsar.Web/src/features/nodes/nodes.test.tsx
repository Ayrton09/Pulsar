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
import { enrollCommand, msiCommand, nodeState } from './nodeStatus';

const operator = {
  ...adminUser,
  permissions: [...adminUser.permissions, 'node.view', 'node.manage', 'node.credentials.manage'],
};
const viewer = { ...adminUser, permissions: ['node.view'] };

const inventory: Schemas['NodeInventoryView'] = {
  hostname: 'BA-GAMES-01',
  osDescription: 'Windows Server 2025 Datacenter',
  osBuild: '26100.4061',
  cpuModel: 'AMD Ryzen 9 7950X',
  logicalCores: 32,
  memoryTotalBytes: 64 * 1024 ** 3,
  serversRoot: 'D:\\PulsarServers',
  agentVersion: '0.1.0',
  protocolVersion: 1,
};
const online: Schemas['NodeSummary'] = {
  id: '0192f1a0-0000-7000-8000-0000000000a1',
  name: 'Buenos Aires 1',
  description: 'Main box',
  publicHost: 'play.example.com',
  status: 'active',
  connected: true,
  lastSeenAt: '2026-10-01T12:00:00Z',
  inventory,
  metrics: {
    at: '2026-10-01T12:00:00Z',
    cpuPercent: 12.5,
    memoryUsedBytes: 16 * 1024 ** 3,
    memoryTotalBytes: 64 * 1024 ** 3,
    uptime: '3.04:05:06.1230000',
    disks: [{ root: 'D:\\', totalBytes: 1000 * 1024 ** 3, freeBytes: 250 * 1024 ** 3, uses: ['servers', 'backups'] }],
  },
  createdAt: '2026-09-01T12:00:00Z',
  panelKeyTrust: 'current',
  enrollmentTokenName: null,
  credentialFingerprint: 'sha256:node-key-fingerprint',
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
const awaiting: Schemas['NodeSummary'] = {
  ...online,
  id: '0192f1a0-0000-7000-8000-0000000000a4',
  name: 'FLEET-01',
  status: 'pendingApproval',
  connected: false,
  metrics: null,
  inventory: { ...inventory, hostname: 'FLEET-01' },
  enrollmentTokenName: 'Intune rollout',
  credentialFingerprint: 'sha256:fleet-01-key',
};
const awaitingToo: Schemas['NodeSummary'] = {
  ...awaiting,
  id: '0192f1a0-0000-7000-8000-0000000000a5',
  name: 'FLEET-02',
  inventory: { ...inventory, hostname: 'FLEET-02' },
  credentialFingerprint: 'sha256:fleet-02-key',
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
    [{ status: 'pendingApproval', connected: false }, 'awaiting'],
  ] as const)('maps %o to %s', (node, state) => {
    expect(nodeState(node)).toBe(state);
  });

  it('builds the installer command for the release folder', () => {
    expect(enrollCommand('https://panel.example.com', 'tok', 'sha256:abc')).toBe(
      'powershell -ExecutionPolicy Bypass -File .\\Install-Pulsar.ps1 -Mode Agent -PanelUrl https://panel.example.com -Token tok -PanelKey sha256:abc',
    );
  });

  it('builds the silent MSI command of a reusable token', () => {
    expect(
      msiCommand('https://panel.example.com', 'pulsar_enroll_x', 'sha256:abc', 'pulsar-agent-1.0.0-windows-x64.msi'),
    ).toBe(
      'msiexec /i pulsar-agent-1.0.0-windows-x64.msi /qn PANELURL=https://panel.example.com TOKEN=pulsar_enroll_x PANELKEY=sha256:abc',
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
      `powershell -ExecutionPolicy Bypass -File .\\Install-Pulsar.ps1 -Mode Agent -PanelUrl ${window.location.origin} -Token one-time-token -PanelKey sha256:panel-key`,
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

describe('bulk enrollment', () => {
  it('starts with nothing selected and approves only the nodes checked', async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/nodes': () => json([online, awaiting, awaitingToo]),
      'POST /api/v1/nodes/approve': async (request) => {
        body = await request.json();
        return json([{ ...awaiting, status: 'active' }]);
      },
    });
    renderAt('/nodes');
    const user = userEvent.setup();

    const card = await screen.findByRole('region', { name: '2 nodes wait for approval' });
    expect(within(screen.getByRole('table', { name: 'Nodes' })).getAllByText('Awaiting approval')).toHaveLength(2);
    const first = within(card).getByRole('checkbox', { name: /^FLEET-01/ });
    expect(first).not.toBeChecked();
    expect(within(card).getByRole('checkbox', { name: /^FLEET-02/ })).not.toBeChecked();
    expect(within(card).getByRole('button', { name: 'Approve 0 nodes' })).toBeDisabled();
    // What to check the machine by: its host name, the token that created it and its key.
    expect(first).toHaveAccessibleName(/FLEET-01, Windows Server 2025 Datacenter/);
    expect(first).toHaveAccessibleName(/Token Intune rollout, key sha256:fleet-01-key/);

    await user.click(first);
    await user.click(within(card).getByRole('button', { name: 'Approve 1 node' }));

    await vi.waitFor(() => expect(body).toEqual({ nodeIds: [awaiting.id] }));
  });

  it('selects every node shown at once', async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/nodes': () => json([awaiting, awaitingToo]),
      'POST /api/v1/nodes/approve': async (request) => {
        body = await request.json();
        return json([]);
      },
    });
    renderAt('/nodes');
    const user = userEvent.setup();

    const card = await screen.findByRole('region', { name: '2 nodes wait for approval' });
    await user.click(within(card).getByRole('checkbox', { name: 'Select all' }));
    expect(within(card).getByRole('checkbox', { name: /^FLEET-02/ })).toBeChecked();
    await user.click(within(card).getByRole('button', { name: 'Approve 2 nodes' }));

    await vi.waitFor(() => expect(body).toEqual({ nodeIds: [awaiting.id, awaitingToo.id] }));
  });

  it('creates a reusable token and shows it once in the MSI and script commands', async () => {
    let body: unknown;
    const token: Schemas['EnrollmentTokenView'] = {
      id: '0192f1a0-0000-7000-8000-0000000000e1',
      name: 'Intune rollout',
      tokenPrefix: 'pulsar_enroll_abcdef',
      maxUses: 25,
      uses: 0,
      requireApproval: true,
      createdAt: '2026-10-09T12:00:00Z',
      expiresAt: '2026-10-16T12:00:00Z',
      lastUsedAt: null,
      revokedAt: null,
      usable: true,
    };
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/nodes': () => json([online]),
      'POST /api/v1/enrollment-tokens': async (request) => {
        body = await request.json();
        return json(
          {
            token,
            value: 'pulsar_enroll_abcdef-secret',
            panelKeyFingerprint: 'sha256:panel-key',
            agentPackage: 'pulsar-agent-1.0.0-windows-x64.msi',
          },
          201,
        );
      },
    });
    renderAt('/nodes');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Create token' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create token' });
    await user.type(within(dialog).getByLabelText('Name'), 'Intune rollout');
    await user.clear(within(dialog).getByLabelText('Machines'));
    await user.type(within(dialog).getByLabelText('Machines'), '25');
    expect(within(dialog).getByRole('checkbox', { name: 'New nodes wait for my approval' })).toBeChecked();
    await user.click(within(dialog).getByRole('button', { name: 'Create' }));

    const msi = await screen.findByLabelText('Agent MSI (Intune, group policy)');
    expect(msi).toHaveTextContent(
      `msiexec /i pulsar-agent-1.0.0-windows-x64.msi /qn PANELURL=${window.location.origin} TOKEN=pulsar_enroll_abcdef-secret PANELKEY=sha256:panel-key`,
    );
    expect(screen.getByLabelText('Installer script (release ZIP)')).toHaveTextContent(
      '-Token pulsar_enroll_abcdef-secret',
    );
    expect(body).toEqual({ name: 'Intune rollout', maxUses: 25, lifetimeHours: 168, requireApproval: true });
  });

  it('lists reusable tokens and revokes one', async () => {
    let revoked = false;
    const token: Schemas['EnrollmentTokenView'] = {
      id: '0192f1a0-0000-7000-8000-0000000000e2',
      name: 'Lab',
      tokenPrefix: 'pulsar_enroll_qwerty',
      maxUses: 10,
      uses: 3,
      requireApproval: false,
      createdAt: '2026-10-09T12:00:00Z',
      expiresAt: '2026-10-16T12:00:00Z',
      lastUsedAt: '2026-10-09T13:00:00Z',
      revokedAt: null,
      usable: true,
    };
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/nodes': () => json([online]),
      'GET /api/v1/enrollment-tokens': () => json([token]),
      [`POST /api/v1/enrollment-tokens/${token.id}/revoke`]: () => {
        revoked = true;
        return new Response(null, { status: 204 });
      },
    });
    vi.stubGlobal('confirm', () => true);
    renderAt('/nodes');
    const user = userEvent.setup();

    const table = await screen.findByRole('table', { name: 'Reusable enrollment tokens' });
    expect(within(table).getByText('3 of 10')).toBeInTheDocument();
    expect(within(table).getByText('Nodes are active at once')).toBeInTheDocument();
    await user.click(within(table).getByRole('button', { name: 'Revoke' }));

    await vi.waitFor(() => expect(revoked).toBe(true));
  });

  it('does not show reusable tokens to whoever cannot manage node credentials', async () => {
    const manager = { ...adminUser, permissions: ['node.view', 'node.manage'] };
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      'GET /api/v1/nodes': () => json([online]),
    });

    renderAt('/nodes');

    expect(await screen.findByRole('table', { name: 'Nodes' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Create token' })).not.toBeInTheDocument();
  });

  it('approves one node from its page', async () => {
    let approved = false;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/nodes/${awaiting.id}`]: () => json({ node: awaiting, credentials: [credential] }),
      [`POST /api/v1/nodes/${awaiting.id}/approve`]: () => {
        approved = true;
        return json({ ...awaiting, status: 'active' });
      },
    });
    renderAt(`/nodes/${awaiting.id}`);
    const user = userEvent.setup();

    expect(await screen.findByText(/its agent cannot connect until it is approved/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Approve' }));

    await vi.waitFor(() => expect(approved).toBe(true));
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
    const disk = screen.getByRole('meter', { name: 'Disk D:\\ (game servers, backups)' });
    expect(disk).toHaveAttribute('aria-valuenow', '75');
    expect(screen.getByText('250 GB free of 1,000 GB')).toBeInTheDocument();
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
