import type { QueryClient } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../api/client';
import { adminUser, fakeApi, json, session } from '../test/fakeApi';
import { App } from './App';
import { initI18n } from './i18n';
import { createQueryClient } from './queryClient';
import { routes } from './routes';

/** Renders with the app's own cache settings, which decide how long finished mutations are kept. */
function renderAt(path: string): QueryClient {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = createQueryClient();
  render(<App router={router} queryClient={queryClient} />);
  return queryClient;
}

/** Whether a mutation the cache still holds (its variables or its answer) contains <paramref name="secret"/>. */
const cacheHolds = (queryClient: QueryClient, secret: string) =>
  JSON.stringify(
    queryClient
      .getMutationCache()
      .getAll()
      .map((mutation) => mutation.state),
  ).includes(secret);

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('one-time secrets', () => {
  it('drops the passwords of a password change once it went through', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/auth/two-factor': () => json({ enabled: false, required: false, recoveryCodesLeft: 0 }),
      'GET /api/v1/account/api-keys': () => json([]),
      'POST /api/v1/auth/password': () => new Response(null, { status: 204 }),
    });
    const queryClient = renderAt('/account');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Current password'), 'the old password');
    await user.type(screen.getByLabelText('New password'), 'a brand new password');
    await user.type(screen.getByLabelText('Confirm password'), 'a brand new password');
    await user.click(screen.getByRole('button', { name: 'Change password' }));

    expect(await screen.findByText(/Password changed/)).toBeInTheDocument();
    await vi.waitFor(() => expect(cacheHolds(queryClient, 'a brand new password')).toBe(false));
    expect(cacheHolds(queryClient, 'the old password')).toBe(false);
  });

  it('drops the password an administrator set for a user', async () => {
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
    fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/users': () => json([friend]),
      'GET /api/v1/roles': () => json([]),
      'GET /api/v1/servers': () => json([]),
      'GET /api/v1/nodes': () => json([]),
      [`POST /api/v1/users/${friend.id}/password`]: () => new Response(null, { status: 204 }),
    });
    const queryClient = renderAt('/admin/users');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit friend' });
    await user.type(within(dialog).getByLabelText('New password'), 'the friend password');
    await user.click(within(dialog).getByRole('button', { name: 'Set password' }));

    expect(await within(dialog).findByText(/Password changed/)).toBeInTheDocument();
    await vi.waitFor(() => expect(cacheHolds(queryClient, 'the friend password')).toBe(false));
  });

  it('drops the SMTP password once saved', async () => {
    const settings: Schemas['NotificationSettingsView'] = {
      panelUrl: null,
      diskLowPercent: 10,
      nodeOfflineMinutes: 3,
      smtp: null,
    };
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['settings.manage'] }),
      'GET /api/v1/notifications/channels': () => json({ channels: [] }),
      'GET /api/v1/notifications/settings': () => json(settings),
      'GET /api/v1/notifications/deliveries': () => json({ deliveries: [] }),
      'GET /api/v1/servers': () => json([]),
      'PUT /api/v1/notifications/settings': () => json(settings),
    });
    const queryClient = renderAt('/admin/notifications');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('SMTP server'), 'smtp.example.com');
    await user.type(screen.getByLabelText('Password'), 'the smtp password');
    await user.click(screen.getByRole('button', { name: 'Save settings' }));

    expect(await screen.findByText('Settings saved.')).toBeInTheDocument();
    await vi.waitFor(() => expect(cacheHolds(queryClient, 'the smtp password')).toBe(false));
  });

  it("drops a node's enrollment token once shown", async () => {
    const node: Schemas['NodeSummary'] = {
      id: '0192f1a0-0000-7000-8000-0000000000a1',
      name: 'Buenos Aires 1',
      description: null,
      publicHost: null,
      status: 'pendingEnrollment',
      connected: false,
      lastSeenAt: null,
      inventory: null,
      metrics: null,
      createdAt: '2026-09-01T12:00:00Z',
      panelKeyTrust: 'current',
      enrollmentTokenName: null,
      credentialFingerprint: null,
      connectedWithCertificate: null,
      agentCanUseCertificate: null,
    };
    fakeApi({
      'GET /api/v1/auth/session': session({
        ...adminUser,
        permissions: ['node.view', 'node.manage', 'node.credentials.manage'],
      }),
      [`GET /api/v1/nodes/${node.id}`]: () => json({ node, credentials: [] }),
      [`GET /api/v1/nodes/${node.id}/allocations`]: () => json([]),
      [`POST /api/v1/nodes/${node.id}/enrollment-tokens`]: () =>
        json({
          token: 'pulsar_enroll_one-time-secret',
          expiresAt: '2026-10-16T12:00:00Z',
          panelKeyFingerprint: 'sha256:panel-key',
          nodeCode: 'pulsar-node-1.one-time-code',
          agentSetupSha256: 'ab'.repeat(32),
          agentSetupAvailable: true,
        }),
    });
    const queryClient = renderAt(`/nodes/${node.id}`);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'New enrollment command' }));

    const dialog = await screen.findByRole('dialog', { name: 'Enroll Buenos Aires 1' });
    expect(dialog).toHaveTextContent('pulsar-node-1.one-time-code');
    await user.click(within(dialog).getByRole('button', { name: 'Advanced: install with PowerShell' }));
    expect(dialog).toHaveTextContent('pulsar_enroll_one-time-secret');
    await vi.waitFor(() => expect(cacheHolds(queryClient, 'pulsar_enroll_one-time-secret')).toBe(false));
    expect(cacheHolds(queryClient, 'pulsar-node-1.one-time-code')).toBe(false);
  });

  it("drops a game database's password once shown", async () => {
    const serverId = '0192f1a0-0000-7000-8000-0000000000e1';
    const hostId = '0192f1a0-0000-7000-8000-0000000000d1';
    const details: Schemas['ServerDetails'] = {
      server: {
        id: serverId,
        shortId: 'k3x7m2qa',
        name: 'Rust',
        nodeId: '0192f1a0-0000-7000-8000-0000000000a1',
        nodeName: 'Buenos Aires 1',
        nodeConnected: true,
        templateId: '0192f1a0-0000-7000-8000-0000000000f1',
        templateName: 'Rust',
        installState: 'installed',
        runtimeState: 'offline',
        desiredState: 'stopped',
        lastStateReason: null,
        lastExitCode: null,
        lastStartedAt: null,
        lastStoppedAt: null,
        activeOperation: null,
        createdAt: '2026-10-02T12:00:00Z',
      },
      description: null,
      autoStart: false,
      restartOnCrash: true,
      templateKey: 'rust',
      templateVersion: '1.0.0',
      canUpdate: true,
      ports: [],
      recentOperations: [],
      limits: { cpuPercent: null, memoryMb: null, databases: 2 },
      hasAddons: false,
    };
    const database: Schemas['ServerDatabaseView'] = {
      id: '0192f1a0-0000-7000-8000-0000000000c1',
      name: 'sk3x7m2qa_main',
      userName: 'sk3x7m2qa_main',
      hostId,
      host: 'LAN MariaDB',
      address: '10.0.0.5',
      port: 3306,
      userHost: '%',
      createdAt: '2026-10-09T12:00:00Z',
      passwordChangedAt: '2026-10-09T12:00:00Z',
    };
    fakeApi({
      'GET /api/v1/auth/session': session({
        ...adminUser,
        permissions: ['server.view', 'server.databases.view', 'server.databases.manage'],
      }),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/databases`]: () =>
        json({ limit: 2, databases: [database], hosts: [{ id: hostId, name: 'LAN MariaDB' }] }),
      [`GET /api/v1/servers/${serverId}/databases/${database.id}/password`]: () =>
        json({ database, password: 'Kq7mPz2xVb9nRt4wLs8yHc3d' }),
    });
    const queryClient = renderAt(`/servers/${serverId}?tab=databases`);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Show password' }));

    expect(await screen.findByRole('region', { name: 'Connection details of sk3x7m2qa_main' })).toHaveTextContent(
      'Kq7mPz2xVb9nRt4wLs8yHc3d',
    );
    await vi.waitFor(() => expect(cacheHolds(queryClient, 'Kq7mPz2xVb9nRt4wLs8yHc3d')).toBe(false));
  });
});
