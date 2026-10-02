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
import { serverActions, serverDisplayState } from './serverStatus';

const operator = {
  ...adminUser,
  permissions: [
    ...adminUser.permissions,
    'node.view',
    'template.view',
    'server.view',
    'server.create',
    'server.start',
    'server.stop',
    'server.restart',
    'server.kill',
    'server.install',
    'server.delete',
  ],
};
const viewer = { ...adminUser, permissions: ['server.view'] };

const installedOffline: Schemas['ServerSummary'] = {
  id: '0192f1a0-0000-7000-8000-0000000000e1',
  shortId: 'k3x7m2qa',
  name: 'Retakes',
  nodeId: '0192f1a0-0000-7000-8000-0000000000a1',
  nodeName: 'Buenos Aires 1',
  nodeConnected: true,
  templateId: '0192f1a0-0000-7000-8000-0000000000f1',
  templateName: 'Pulsar test server',
  installState: 'installed',
  runtimeState: 'offline',
  desiredState: 'stopped',
  lastStateReason: null,
  lastExitCode: null,
  lastStartedAt: null,
  lastStoppedAt: null,
  activeOperation: null,
  createdAt: '2026-10-02T12:00:00Z',
};
const running: Schemas['ServerSummary'] = {
  ...installedOffline,
  id: '0192f1a0-0000-7000-8000-0000000000e2',
  name: 'Surf',
  runtimeState: 'running',
  desiredState: 'running',
  lastStateReason: 'ready',
  lastStartedAt: '2026-10-02T12:05:00Z',
};
const crashLoop: Schemas['ServerSummary'] = {
  ...installedOffline,
  id: '0192f1a0-0000-7000-8000-0000000000e3',
  name: 'Zombies',
  runtimeState: 'crashed',
  lastStateReason: 'crashLoop',
  lastExitCode: 3,
  nodeConnected: false,
};
const installOperation: Schemas['OperationView'] = {
  id: '0192f1a0-0000-7000-8000-0000000000b1',
  type: 'serverInstall',
  status: 'running',
  stage: 'installing',
  percent: null,
  errorCode: null,
  errorMessage: null,
  createdAt: '2026-10-02T12:00:00Z',
  completedAt: null,
  cancellable: true,
  cancelRequested: false,
};
const installing: Schemas['ServerSummary'] = {
  ...installedOffline,
  id: '0192f1a0-0000-7000-8000-0000000000e4',
  name: 'Fresh',
  installState: 'installing',
  activeOperation: installOperation,
};

function details(
  server: Schemas['ServerSummary'],
  operations: Schemas['OperationView'][] = [],
): Schemas['ServerDetails'] {
  return {
    server,
    description: null,
    autoStart: false,
    restartOnCrash: true,
    templateKey: 'pulsar-test',
    templateVersion: '1.0.0',
    canUpdate: false,
    ports: [],
    recentOperations: operations,
  };
}

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

describe('server status', () => {
  it.each([
    [installing, 'installing'],
    [{ ...installing, activeOperation: { ...installOperation, type: 'serverDelete' } }, 'deleting'],
    [{ ...installedOffline, installState: 'failed' }, 'installFailed'],
    [{ ...installedOffline, installState: 'notInstalled' }, 'notInstalled'],
    [installedOffline, 'offline'],
    [running, 'online'],
    [crashLoop, 'crashed'],
    [{ ...running, runtimeState: 'stopping' }, 'stopping'],
  ] as const)('shows %#: %s', (server, state) => {
    expect(serverDisplayState(server)).toBe(state);
  });

  it('offers only the actions that make sense in each state', () => {
    expect(serverActions(installedOffline)).toEqual({
      start: true,
      stop: false,
      restart: false,
      kill: false,
      reinstall: true,
      delete: true,
    });
    expect(serverActions(running)).toEqual({
      start: false,
      stop: true,
      restart: true,
      kill: true,
      reinstall: false,
      delete: true,
    });
    expect(serverActions(installing)).toMatchObject({ start: false, reinstall: false, delete: false });
    // Nothing can reach a server whose node is disconnected.
    expect(Object.values(serverActions(crashLoop)).some(Boolean)).toBe(false);
  });
});

describe('servers page', () => {
  it('lists servers with their state, the reason and their node', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/servers': () => json([running, crashLoop, installing]),
    });

    renderAt('/servers');

    const table = await screen.findByRole('table', { name: 'Servers' });
    const rows = within(table).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('Surf');
    expect(rows[1]).toHaveTextContent('Online');
    expect(rows[2]).toHaveTextContent('Last known: Crashed');
    expect(rows[2]).toHaveTextContent('Node disconnected');
    expect(rows[2]).toHaveTextContent('Crashed too many times in a row');
    expect(rows[3]).toHaveTextContent('Installing');
    expect(screen.getByRole('link', { name: 'Servers' })).toBeInTheDocument();
  });

  it('creates a server on a connected node and opens it', async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/servers': () => json([]),
      'GET /api/v1/nodes': () =>
        json([
          { id: installedOffline.nodeId, name: 'Buenos Aires 1', connected: true, status: 'active' },
          { id: '0192f1a0-0000-7000-8000-0000000000a9', name: 'Offline box', connected: false, status: 'active' },
        ]),
      'GET /api/v1/templates': () =>
        json([
          {
            id: installedOffline.templateId,
            key: 'pulsar-test',
            version: '1.0.0',
            name: 'Pulsar test server',
            description: null,
            isTestGame: true,
          },
        ]),
      'POST /api/v1/servers': async (request) => {
        body = await request.json();
        return json(details(installing, [installOperation]), 201);
      },
      [`GET /api/v1/servers/${installing.id}`]: () => json(details(installing, [installOperation])),
    });
    const router = renderAt('/servers');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Create server' }));
    await user.type(screen.getByLabelText('Name'), 'Fresh');
    const nodes = screen.getByLabelText('Node');
    expect(within(nodes).queryByRole('option', { name: 'Offline box' })).not.toBeInTheDocument();
    await user.selectOptions(nodes, 'Buenos Aires 1');
    await user.selectOptions(await screen.findByLabelText('Game'), 'Pulsar test server 1.0.0');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByRole('heading', { name: 'Fresh' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe(`/servers/${installing.id}`);
    expect(body).toEqual({
      name: 'Fresh',
      description: null,
      nodeId: installedOffline.nodeId,
      templateId: installedOffline.templateId,
    });
  });

  it('does not offer to create servers to users who can only view them', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      'GET /api/v1/servers': () => json([running]),
    });

    renderAt('/servers');

    expect(await screen.findByRole('table', { name: 'Servers' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Create server' })).not.toBeInTheDocument();
  });
});

describe('server page', () => {
  it('starts a stopped server', async () => {
    const start = vi.fn(() => json({ status: 'accepted', errorCode: null }));
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${installedOffline.id}`]: () => json(details(installedOffline)),
      [`POST /api/v1/servers/${installedOffline.id}/start`]: start,
    });
    renderAt(`/servers/${installedOffline.id}`);
    const user = userEvent.setup();

    const actions = await screen.findByRole('region', { name: 'Server actions' });
    expect(within(actions).getByRole('button', { name: 'Stop' })).toBeDisabled();
    await user.click(within(actions).getByRole('button', { name: 'Start' }));

    expect(start).toHaveBeenCalledOnce();
  });

  it('asks before killing and reports a command the node refused', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${running.id}`]: () => json(details(running)),
      [`POST /api/v1/servers/${running.id}/kill`]: () => json({ status: 'busy', errorCode: 'server.busy' }),
    });
    renderAt(`/servers/${running.id}`);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Kill' }));

    expect(window.confirm).toHaveBeenCalledWith('Kill Surf now? Its processes end without saving.');
    expect(await screen.findByRole('status')).toHaveTextContent(
      'The node did not accept the command (busy server.busy).',
    );
  });

  it('shows the operations and why an install failed', async () => {
    const failed: Schemas['OperationView'] = {
      ...installOperation,
      status: 'failed',
      errorCode: 'install.test_game_unavailable',
      errorMessage: 'This agent was not started with the test game server.',
      completedAt: '2026-10-02T12:01:00Z',
    };
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${installedOffline.id}`]: () =>
        json(details({ ...installedOffline, installState: 'failed' }, [failed])),
    });

    renderAt(`/servers/${installedOffline.id}`);

    const operations = await screen.findByRole('table', { name: 'Recent operations' });
    expect(operations).toHaveTextContent('Install');
    expect(operations).toHaveTextContent('Failed');
    expect(operations).toHaveTextContent('This agent was not started with the test game server.');
    expect(screen.getAllByText('Install failed').length).toBeGreaterThan(0);
  });

  it('explains a node conflict when deleting', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${installedOffline.id}`]: () => json(details(installedOffline)),
      [`DELETE /api/v1/servers/${installedOffline.id}`]: () => problem(409, 'node.not_connected'),
    });
    renderAt(`/servers/${installedOffline.id}`);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Delete' }));

    expect(await screen.findByRole('alert')).toHaveTextContent("The node's agent is not connected.");
  });

  it('hides the actions a viewer cannot use', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      [`GET /api/v1/servers/${running.id}`]: () => json(details(running)),
    });

    renderAt(`/servers/${running.id}`);

    expect(await screen.findByRole('heading', { name: 'Surf' })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Server actions' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
  });
});
