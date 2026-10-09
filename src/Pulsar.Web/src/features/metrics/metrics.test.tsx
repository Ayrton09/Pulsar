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
import { niceMax, splitRuns } from './chartMath';

const serverId = '0192f1a0-0000-7000-8000-0000000000e1';
const viewer = { ...adminUser, permissions: ['server.view'] };

const details: Schemas['ServerDetails'] = {
  server: {
    id: serverId,
    shortId: 'k3x7m2qa',
    name: 'Retakes',
    nodeId: '0192f1a0-0000-7000-8000-0000000000a1',
    nodeName: 'Buenos Aires 1',
    nodeConnected: true,
    templateId: '0192f1a0-0000-7000-8000-0000000000f1',
    templateName: 'Counter-Strike 2',
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
  templateKey: 'cs2',
  templateVersion: '1.0.0',
  canUpdate: true,
  ports: [],
  recentOperations: [],
  limits: { cpuPercent: null, memoryMb: null, databases: 2 },
  hasAddons: false,
};

function point(at: string, cpu: number, players: number | null = null): Schemas['ServerMetricPoint'] {
  return {
    at,
    cpuAverage: cpu,
    cpuPeak: cpu * 2,
    memoryAverage: 512 * 1024 ** 2,
    memoryPeak: 600 * 1024 ** 2,
    playersAverage: players,
    playersPeak: players == null ? null : Math.ceil(players),
  };
}

function history(
  range: Schemas['MetricRange'],
  points: Schemas['ServerMetricPoint'][],
): Schemas['ServerMetricHistory'] {
  return { range, from: '2026-10-09T10:00:00Z', to: '2026-10-09T11:00:00Z', stepSeconds: 60, points };
}

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
  render(<App router={router} queryClient={queryClient} />);
}

function historyCard(): HTMLElement {
  return screen.getByRole('heading', { name: 'Usage history' }).closest('section') as HTMLElement;
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('usage history', () => {
  it('charts CPU and memory, and players only when the game answered queries', async () => {
    const ranges: string[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/metrics`]: (request) => {
        const range = new URL(request.url).searchParams.get('range') as Schemas['MetricRange'];
        ranges.push(range);
        return json(
          range === 'hour'
            ? history('hour', [point('2026-10-09T10:00:00Z', 10), point('2026-10-09T10:01:00Z', 20)])
            : history(range, [point('2026-10-09T10:00:00Z', 5, 3.5)]),
        );
      },
    });

    renderAt(`/servers/${serverId}`);

    await screen.findByRole('group', { name: /^CPU over time/ });
    expect(within(historyCard()).getByRole('group', { name: /^Memory over time/ })).toBeInTheDocument();
    expect(within(historyCard()).queryByRole('group', { name: /^Players over time/ })).not.toBeInTheDocument();
    expect(within(historyCard()).getByRole('button', { name: '1 h' })).toHaveAttribute('aria-pressed', 'true');
    // The points are also a table.
    const cpuTable = within(historyCard()).getByRole('table', { name: 'CPU by time' });
    expect(within(cpuTable).getAllByRole('row')).toHaveLength(3);
    expect(cpuTable).toHaveTextContent('20 %');
    expect(cpuTable).toHaveTextContent('40 %');

    await userEvent.click(within(historyCard()).getByRole('button', { name: '7 d' }));

    await within(historyCard()).findByRole('group', { name: /^Players over time/ });
    expect(ranges).toEqual(['hour', 'week']);
    expect(within(historyCard()).getByRole('table', { name: 'Players by time' })).toHaveTextContent('3.5');
  });

  it('reads each point out with the arrow keys', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/metrics`]: () =>
        json(history('hour', [point('2026-10-09T10:00:00Z', 10), point('2026-10-09T10:01:00Z', 20)])),
    });

    renderAt(`/servers/${serverId}`);

    const chart = await screen.findByRole('group', { name: /^CPU over time/ });
    chart.focus();
    await userEvent.keyboard('{End}');
    await waitFor(() => expect(chart).toHaveAccessibleDescription(/average 20 %, peak 40 %$/));
    await userEvent.keyboard('{ArrowLeft}');
    await waitFor(() => expect(chart).toHaveAccessibleDescription(/average 10 %, peak 20 %$/));
    await userEvent.keyboard('{Escape}');
    await waitFor(() => expect(chart).not.toHaveAccessibleDescription(/average/));
  });

  it('says when nothing was recorded', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/metrics`]: () => json(history('hour', [])),
    });

    renderAt(`/servers/${serverId}`);

    expect(await screen.findByText(/No usage recorded in this period yet/)).toBeInTheDocument();
  });
});

describe('node usage history', () => {
  const nodeId = '0192f1a0-0000-7000-8000-0000000000a1';
  const node: Schemas['NodeSummary'] = {
    id: nodeId,
    name: 'Buenos Aires 1',
    description: null,
    publicHost: null,
    status: 'active',
    connected: true,
    lastSeenAt: '2026-10-09T10:00:00Z',
    inventory: null,
    metrics: null,
    createdAt: '2026-09-01T12:00:00Z',
    panelKeyTrust: 'current',
  };
  const nodeViewer = { ...adminUser, permissions: ['node.view'] };

  function nodePoint(at: string, cpu: number, usedGb: number): Schemas['NodeMetricPoint'] {
    return {
      at,
      cpuAverage: cpu,
      cpuPeak: cpu * 2,
      memoryUsedAverage: usedGb * 1024 ** 3,
      memoryUsedPeak: (usedGb + 1) * 1024 ** 3,
      memoryTotal: 64 * 1024 ** 3,
    };
  }

  function renderNode(points: Schemas['NodeMetricPoint'][]) {
    fakeApi({
      'GET /api/v1/auth/session': session(nodeViewer),
      [`GET /api/v1/nodes/${nodeId}`]: () => json({ node, credentials: [] }),
      [`GET /api/v1/nodes/${nodeId}/allocations`]: () => json([]),
      [`GET /api/v1/nodes/${nodeId}/metrics`]: () =>
        json({ range: 'hour', from: '2026-10-09T10:00:00Z', to: '2026-10-09T11:00:00Z', stepSeconds: 60, points }),
    });
    renderAt(`/nodes/${nodeId}`);
  }

  it('charts CPU and memory, with the machine’s whole memory as the top', async () => {
    renderNode([nodePoint('2026-10-09T10:00:00Z', 10, 12), nodePoint('2026-10-09T10:01:00Z', 30, 16)]);

    await screen.findByRole('group', { name: /^CPU over time/ });
    const memory = within(historyCard()).getByRole('group', { name: /^Memory over time/ });
    // 64 GB, not a round number above 16 GB.
    const axis = (memory.closest('figure') as HTMLElement).querySelector('.flex-col') as HTMLElement;
    expect(axis).toHaveTextContent(/^64 GB32 GB0 GB$/);
    expect(within(historyCard()).getByRole('table', { name: 'CPU by time' })).toHaveTextContent('30 %');
  });

  it('says when nothing was recorded', async () => {
    renderNode([]);

    expect(
      await screen.findByText('No usage recorded in this period yet. It is recorded while the node is connected.'),
    ).toBeInTheDocument();
    expect(within(historyCard()).queryByRole('group', { name: /over time/ })).not.toBeInTheDocument();
  });
});

describe('chart math', () => {
  it('rounds the axis up to 1, 2, 2.5 or 5 times a power of ten', () => {
    expect([0, 0.3, 1, 1.2, 2.2, 4, 7, 99, 101].map(niceMax)).toEqual([1, 0.5, 1, 2, 2.5, 5, 10, 100, 200]);
  });

  it('breaks the line where points are missing', () => {
    const at = (minute: number) => ({ at: `2026-10-09T10:0${minute}:00Z`, average: minute, peak: minute });
    const runs = splitRuns([at(0), at(1), at(4), at(5), at(7)], 60);
    expect(runs.map((run) => run.points.map((p) => p.average))).toEqual([[0, 1], [4, 5], [7]]);
  });
});
