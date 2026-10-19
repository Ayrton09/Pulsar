import { QueryClient } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, session } from '../../test/fakeApi';
import { connections, openedConnection } from '../../test/fakeSignalR';

const server = (
  id: string,
  name: string,
  change: Partial<Schemas['ServerSummary']> = {},
): Schemas['ServerSummary'] => ({
  id,
  shortId: 'k3x7m2qa',
  name,
  nodeId: '0192f1a0-0000-7000-8000-0000000000a1',
  nodeName: 'Buenos Aires 1',
  nodeConnected: true,
  templateId: '0192f1a0-0000-7000-8000-0000000000f1',
  templateName: 'Pulsar test server',
  templateKey: 'pulsar-test',
  gameAddress: null,
  installState: 'installed',
  runtimeState: 'running',
  desiredState: 'running',
  lastStateReason: 'ready',
  lastExitCode: null,
  lastStartedAt: null,
  lastStoppedAt: null,
  activeOperation: null,
  createdAt: '2026-10-02T12:00:00Z',
  ...change,
});

const node = (
  id: string,
  name: string,
  connected: boolean,
  cores: number,
  cpu: number,
  usedGb: number,
  status: Schemas['NodeStatus'] = 'active',
) => ({
  id,
  name,
  description: null,
  publicHost: null,
  status,
  connected,
  lastSeenAt: null,
  inventory: { logicalCores: cores },
  metrics: connected
    ? {
        at: '2026-10-02T12:00:00Z',
        cpuPercent: cpu,
        memoryUsedBytes: usedGb * 1024 ** 3,
        memoryTotalBytes: 16 * 1024 ** 3,
        uptime: '01:00:00',
      }
    : null,
  createdAt: '2026-10-02T12:00:00Z',
  panelKeyTrust: 'current',
  enrollmentTokenName: null,
  credentialFingerprint: null,
});

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('dashboard', () => {
  it('sums up servers, nodes and their usage, and lists what needs attention', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['server.view', 'node.view'] }),
      'GET /api/v1/servers': () =>
        json([
          server('0192f1a0-0000-7000-8000-0000000000e1', 'Surf'),
          server('0192f1a0-0000-7000-8000-0000000000e2', 'Zombies', {
            runtimeState: 'crashed',
            lastStateReason: 'crashLoop',
          }),
          server('0192f1a0-0000-7000-8000-0000000000e3', 'Retakes', { runtimeState: 'offline' }),
        ]),
      'GET /api/v1/nodes': () =>
        json([
          node('0192f1a0-0000-7000-8000-0000000000a1', 'Buenos Aires 1', true, 4, 10, 4),
          node('0192f1a0-0000-7000-8000-0000000000a2', 'Buenos Aires 2', true, 12, 50, 8),
          node('0192f1a0-0000-7000-8000-0000000000a3', 'Córdoba', false, 8, 0, 0),
        ]),
    });
    const before = connections.length;
    render(<App router={createMemoryRouter(routes, { initialEntries: ['/'] })} queryClient={new QueryClient()} />);
    const live = await openedConnection(before);

    expect(await screen.findByText('1 of 3 running')).toBeInTheDocument();
    expect(await screen.findByText('2 of 3 online')).toBeInTheDocument();
    expect(screen.getByText('40 % CPU')).toBeInTheDocument(); // (4 × 10 + 12 × 50) / 16 cores
    expect(screen.getByText('38 %')).toBeInTheDocument(); // 12 of 32 GB
    expect(screen.getByRole('link', { name: 'Zombies keeps crashing and was not restarted again' })).toHaveAttribute(
      'href',
      '/servers/0192f1a0-0000-7000-8000-0000000000e2',
    );
    expect(screen.getByRole('link', { name: 'Node Córdoba is offline' })).toBeInTheDocument();
    await waitFor(() => expect(live.calls('WatchServers')).toHaveLength(1));
    expect(live.calls('WatchNodes')).toHaveLength(1);
  });

  it('shows up to six servers as cards, the running ones first', async () => {
    const stopped = Array.from({ length: 6 }, (_, i) =>
      server(`0192f1a0-0000-7000-8000-00000000010${i}`, `Stopped ${i + 1}`, { runtimeState: 'offline' }),
    );
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['server.view'] }),
      'GET /api/v1/servers': () => json([...stopped, server('0192f1a0-0000-7000-8000-0000000000e1', 'Surf')]),
    });
    render(<App router={createMemoryRouter(routes, { initialEntries: ['/'] })} queryClient={new QueryClient()} />);

    const section = await screen.findByRole('region', { name: 'Your servers' });
    const cards = within(section).getAllByRole('listitem');
    expect(cards).toHaveLength(6);
    expect(within(cards[0] as HTMLElement).getByRole('heading', { level: 3 })).toHaveTextContent('Surf');
    expect(within(section).getByRole('link', { name: 'All servers (7)' })).toHaveAttribute('href', '/servers');
  });

  it('counts a disabled node apart from the offline ones', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['node.view'] }),
      'GET /api/v1/nodes': () =>
        json([
          node('0192f1a0-0000-7000-8000-0000000000a1', 'Buenos Aires 1', true, 4, 10, 4),
          node('0192f1a0-0000-7000-8000-0000000000a2', 'Retired', false, 4, 0, 0, 'disabled'),
          node('0192f1a0-0000-7000-8000-0000000000a3', 'Córdoba', false, 8, 0, 0),
        ]),
    });
    render(<App router={createMemoryRouter(routes, { initialEntries: ['/'] })} queryClient={new QueryClient()} />);

    expect(await screen.findByText('1 of 2 online')).toBeInTheDocument();
    expect(screen.getByText('Offline: 1 · Disabled: 1')).toBeInTheDocument();
    // What the count says matches what needs attention.
    expect(screen.getByRole('link', { name: 'Node Córdoba is offline' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Retired/ })).not.toBeInTheDocument();
  });

  it('shows nothing it would need permissions for', async () => {
    fakeApi({ 'GET /api/v1/auth/session': session({ ...adminUser, permissions: [] }) });
    const before = connections.length;
    render(<App router={createMemoryRouter(routes, { initialEntries: ['/'] })} queryClient={new QueryClient()} />);
    const live = await openedConnection(before);

    expect(await screen.findByText(/Nothing to show yet/)).toBeInTheDocument();
    expect(live.calls('WatchServers')).toHaveLength(0);
  });
});
