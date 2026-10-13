import { QueryClient } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, problem, session } from '../../test/fakeApi';
import { connections, openedConnection } from '../../test/fakeSignalR';
import { serverActions, serverDisplayState } from './serverStatus';

// CodeMirror needs a real layout: the editor is a plain text area with the same contract.
vi.mock('../files/CodeEditor', () => ({
  default: ({ label, initial, onChange }: { label: string; initial: string; onChange: (text: string) => void }) => (
    <textarea aria-label={label} defaultValue={initial} onChange={(e) => onChange(e.target.value)} />
  ),
}));

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
    limits: { cpuPercent: null, memoryMb: null, databases: 2 },
    hasAddons: false,
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
            source: 'bundled',
          },
        ]),
      [`GET /api/v1/templates/${installedOffline.templateId}`]: () =>
        json({
          template: {
            id: installedOffline.templateId,
            key: 'pulsar-test',
            version: '1.0.0',
            name: 'Pulsar test server',
            description: null,
            isTestGame: true,
            source: 'bundled',
          },
          minDiskGb: null,
          minMemoryMb: null,
          variables: [],
          ports: [],
          allowExtraArguments: false,
          canUpdate: false,
          capabilities: {
            executable: 'bin/Pulsar.TestGameServer.exe',
            steamAppIds: [],
            configFiles: [],
            consoleMode: 'pty',
          },
        }),
      [`GET /api/v1/nodes/${installedOffline.nodeId}/allocations`]: () => json([]),
      'POST /api/v1/servers': async (request) => {
        body = await request.json();
        return json(details(installing, [installOperation]), 201);
      },
      [`GET /api/v1/servers/${installing.id}`]: () => json(details(installing, [installOperation])),
    });
    const before = connections.length;
    const router = renderAt('/servers');
    const user = userEvent.setup();
    const live = await openedConnection(before);

    await user.click(await screen.findByRole('button', { name: 'Create server' }));
    // Nodes connecting or dropping while the dialog is open update its list.
    await waitFor(() => expect(live.calls('WatchNodes')).toHaveLength(1));
    await user.type(screen.getByLabelText('Name'), 'Fresh');
    const nodes = screen.getByLabelText('Node');
    expect(within(nodes).queryByRole('option', { name: 'Offline box' })).not.toBeInTheDocument();
    await user.selectOptions(nodes, 'Buenos Aires 1');
    await user.selectOptions(await screen.findByLabelText('Game'), 'Pulsar test server 1.0.0');
    await user.click(await screen.findByRole('button', { name: 'Next' }));
    expect(await screen.findByText('This game has no settings.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByText('This game does not listen on any port.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByRole('heading', { name: 'Fresh' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe(`/servers/${installing.id}`);
    expect(body).toEqual({
      name: 'Fresh',
      description: null,
      nodeId: installedOffline.nodeId,
      templateId: installedOffline.templateId,
      variables: {},
      ports: {},
    });
  });

  it('says when the chosen game cannot be loaded, and tries again', async () => {
    let fail = true;
    const game = {
      id: installedOffline.templateId,
      key: 'pulsar-test',
      version: '1.0.0',
      name: 'Pulsar test server',
      description: null,
      isTestGame: true,
      source: 'bundled',
    };
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/servers': () => json([]),
      'GET /api/v1/nodes': () =>
        json([{ id: installedOffline.nodeId, name: 'Buenos Aires 1', connected: true, status: 'active' }]),
      'GET /api/v1/templates': () => json([game]),
      [`GET /api/v1/templates/${installedOffline.templateId}`]: () =>
        fail
          ? problem(500, 'unexpected')
          : json({
              template: game,
              minDiskGb: null,
              minMemoryMb: null,
              variables: [],
              ports: [],
              allowExtraArguments: false,
              canUpdate: false,
              capabilities: { executable: 'x.exe', steamAppIds: [], configFiles: [], consoleMode: 'pty' },
            }),
    });
    renderAt('/servers');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Create server' }));
    await user.type(screen.getByLabelText('Name'), 'Fresh');
    await user.selectOptions(screen.getByLabelText('Node'), 'Buenos Aires 1');
    await user.selectOptions(await screen.findByLabelText('Game'), 'Pulsar test server 1.0.0');

    const dialog = screen.getByRole('dialog');
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Something went wrong');
    fail = false;
    await user.click(within(dialog).getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(within(dialog).queryByRole('alert')).not.toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByText('This game has no settings.')).toBeInTheDocument();
  });

  it('does not ask for a port that comes with another one', async () => {
    let body: unknown;
    const template = {
      id: installedOffline.templateId,
      key: 'valheim',
      version: '1.0.0',
      name: 'Valheim',
      description: null,
      isTestGame: false,
      source: 'bundled',
    };
    const allocation = (id: string, port: number) => ({
      id,
      ipAddress: '0.0.0.0',
      port,
      serverId: null,
      serverName: null,
      portName: null,
      notes: null,
    });
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/servers': () => json([]),
      'GET /api/v1/nodes': () =>
        json([{ id: installedOffline.nodeId, name: 'Buenos Aires 1', connected: true, status: 'active' }]),
      'GET /api/v1/templates': () => json([template]),
      [`GET /api/v1/templates/${installedOffline.templateId}`]: () =>
        json({
          template,
          minDiskGb: null,
          minMemoryMb: null,
          variables: [],
          ports: [
            { name: 'game', label: 'Game', protocols: ['udp'], default: 2456, required: true, description: null },
            {
              name: 'query',
              label: 'Server list query',
              protocols: ['udp'],
              default: 2457,
              required: true,
              description: null,
              follows: 'game',
              offset: 1,
            },
          ],
          allowExtraArguments: false,
          canUpdate: false,
          capabilities: {
            executable: 'valheim_server.exe',
            steamAppIds: [896660],
            configFiles: [],
            consoleMode: 'pty',
          },
        }),
      [`GET /api/v1/nodes/${installedOffline.nodeId}/allocations`]: () =>
        json([
          allocation('0192f1a0-0000-7000-8000-0000000000c1', 2456),
          allocation('0192f1a0-0000-7000-8000-0000000000c2', 2457),
        ]),
      'POST /api/v1/servers': async (request) => {
        body = await request.json();
        return json(details(installing, [installOperation]), 201);
      },
      [`GET /api/v1/servers/${installing.id}`]: () => json(details(installing, [installOperation])),
    });
    renderAt('/servers');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Create server' }));
    await user.type(screen.getByLabelText('Name'), 'Viking');
    await user.selectOptions(screen.getByLabelText('Node'), 'Buenos Aires 1');
    await user.selectOptions(await screen.findByLabelText('Game'), 'Valheim 1.0.0');
    await user.click(await screen.findByRole('button', { name: 'Next' }));
    await user.click(await screen.findByRole('button', { name: 'Next' }));
    await user.selectOptions(await screen.findByLabelText('Game *'), '0.0.0.0:2456');

    expect(screen.getByText('comes with Game: its port + 1.')).toBeInTheDocument();
    expect(screen.queryByLabelText(/Server list query/)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Create' }));
    await screen.findByRole('heading', { name: 'Fresh' });
    expect(body).toMatchObject({ name: 'Viking', ports: { game: '0192f1a0-0000-7000-8000-0000000000c1' } });
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

  it('moves between its sections with the arrow keys, as tabs do', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${installedOffline.id}`]: () => json(details(installedOffline)),
    });
    renderAt(`/servers/${installedOffline.id}`);
    const user = userEvent.setup();

    const tabs = await screen.findByRole('tablist', { name: 'Server sections' });
    const overview = within(tabs).getByRole('tab', { name: 'Overview' });
    const second = within(tabs).getByRole('tab', { name: 'Settings' });
    // Only the open section is in the Tab order; the arrows move between them.
    expect(overview).toHaveAttribute('tabindex', '0');
    expect(second).toHaveAttribute('tabindex', '-1');
    overview.focus();

    await user.keyboard('{ArrowRight}');
    expect(second).toHaveFocus();
    expect(second).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tabpanel')).toHaveAttribute('aria-labelledby', second.id);

    await user.keyboard('{End}');
    expect(within(tabs).getAllByRole('tab').at(-1)).toHaveFocus();
    await user.keyboard('{ArrowRight}');
    expect(overview).toHaveFocus();
    expect(overview).toHaveAttribute('aria-selected', 'true');
  });

  it('moves on from the focused tab, even when the open section did not change (a blocked leave)', async () => {
    const editor = { ...adminUser, permissions: ['server.view', 'server.files.view', 'server.files.edit'] };
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${installedOffline.id}`]: () => json(details(installedOffline)),
      [`GET /api/v1/servers/${installedOffline.id}/files/content`]: () =>
        new Response('sv_cheats 0', { headers: { ETag: '"v1"' } }),
    });
    // Unsaved changes in the editor: leaving asks first, and the user stays.
    const stay = vi.spyOn(window, 'confirm').mockReturnValue(false);
    renderAt(`/servers/${installedOffline.id}?tab=files&file=server.cfg`);
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Contents of server.cfg'), ' // edited');
    const tabs = screen.getByRole('tablist', { name: 'Server sections' });
    const [files, settings, network] = ['Files', 'Settings', 'Network'].map((name) =>
      within(tabs).getByRole('tab', { name }),
    );
    files?.focus();

    await user.keyboard('{ArrowRight}');
    await waitFor(() => expect(stay).toHaveBeenCalledTimes(1));
    // Refused: Files is still the open section, but focus moved on to the next tab...
    expect(settings).toHaveFocus();
    expect(files).toHaveAttribute('aria-selected', 'true');

    // ...and the next arrow goes on from there, not back to the one after the open section.
    await user.keyboard('{ArrowRight}');
    await waitFor(() => expect(stay).toHaveBeenCalledTimes(2));
    expect(network).toHaveFocus();
    expect(files).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByLabelText('Contents of server.cfg')).toHaveValue('sv_cheats 0 // edited');
  });

  it('leaves arrow keys with a modifier to the browser (Alt+Left goes back)', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${installedOffline.id}`]: () => json(details(installedOffline)),
    });
    renderAt(`/servers/${installedOffline.id}`);
    const user = userEvent.setup();

    const tabs = await screen.findByRole('tablist', { name: 'Server sections' });
    const overview = within(tabs).getByRole('tab', { name: 'Overview' });
    overview.focus();

    await user.keyboard('{Alt>}{ArrowRight}{/Alt}');
    await user.keyboard('{Control>}{End}{/Control}');
    expect(overview).toHaveFocus();
    expect(overview).toHaveAttribute('aria-selected', 'true');
  });

  it('shows what a role on this server allows, and only that', async () => {
    // No global permission: a role on this one server.
    const friend = {
      ...adminUser,
      permissions: [],
      serverPermissions: [
        { id: installedOffline.id, permissions: ['server.view', 'server.start', 'server.console.view'] },
      ],
    };
    fakeApi({
      'GET /api/v1/auth/session': session(friend),
      [`GET /api/v1/servers/${installedOffline.id}`]: () => json(details(installedOffline)),
    });
    renderAt(`/servers/${installedOffline.id}`);

    const actions = await screen.findByRole('region', { name: 'Server actions' });
    expect(within(actions).getByRole('button', { name: 'Start' })).toBeEnabled();
    expect(within(actions).queryByRole('button', { name: 'Kill' })).not.toBeInTheDocument();
    const tabs = screen.getByRole('tablist', { name: 'Server sections' });
    expect(within(tabs).getByRole('tab', { name: 'Console' })).toBeInTheDocument();
    expect(within(tabs).queryByRole('tab', { name: 'Files' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete server' })).not.toBeInTheDocument();
    const navigation = screen.getByRole('navigation');
    expect(within(navigation).getByRole('link', { name: 'Servers' })).toBeInTheDocument();
    expect(within(navigation).queryByRole('link', { name: 'Nodes' })).not.toBeInTheDocument();
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
      'Another operation is running on this server. Wait for it to finish.',
    );
  });

  it('says why the agent refused a command, or which code it gave', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${running.id}`]: () => json(details(running)),
      [`POST /api/v1/servers/${running.id}/restart`]: () =>
        json({ status: 'rejected', errorCode: 'server.host_incompatible' }),
      [`POST /api/v1/servers/${running.id}/stop`]: () =>
        json({ status: 'unsupportedCommand', errorCode: 'server.something_new' }),
    });
    renderAt(`/servers/${running.id}`);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Restart' }));
    expect(await screen.findByRole('status')).toHaveTextContent(
      "The server's game still runs under an earlier version of the agent, which cannot manage it. Stop or start the server to end that game (it is killed, without saving).",
    );

    await user.click(screen.getByRole('button', { name: 'Stop' }));
    await waitFor(() =>
      expect(screen.getByRole('status')).toHaveTextContent(
        'The node did not accept the command (unknown command server.something_new).',
      ),
    );
  });

  it('shows the operations and why one failed, in the user language', async () => {
    const failed: Schemas['OperationView'] = {
      ...installOperation,
      status: 'failed',
      errorCode: 'install.test_game_unavailable',
      errorMessage: 'This agent was not started with the test game server.',
      completedAt: '2026-10-02T12:01:00Z',
    };
    const interrupted: Schemas['OperationView'] = {
      ...installOperation,
      id: '0192f1a0-0000-7000-8000-0000000000b2',
      type: 'serverUpdate',
      status: 'interrupted',
      errorCode: 'operation.interrupted',
      errorMessage: 'The agent restarted or lost the operation.',
      completedAt: '2026-10-02T12:02:00Z',
    };
    const unknown: Schemas['OperationView'] = {
      ...failed,
      id: '0192f1a0-0000-7000-8000-0000000000b3',
      errorCode: 'install.something_new',
      errorMessage: 'Something new went wrong.',
    };
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${installedOffline.id}`]: () =>
        json(details({ ...installedOffline, installState: 'failed' }, [failed, interrupted, unknown])),
    });

    renderAt(`/servers/${installedOffline.id}`);

    const operations = await screen.findByRole('table', { name: 'Recent operations' });
    expect(operations).toHaveTextContent('Install');
    expect(operations).toHaveTextContent('Failed');
    // The code's translation, the code itself only as a tooltip.
    const reason = within(operations).getByText(
      "The node's agent was not started with the test game, so it cannot install it.",
    );
    expect(reason).toHaveAttribute('title', 'install.test_game_unavailable');
    expect(operations).not.toHaveTextContent('This agent was not started with the test game server.');
    expect(operations).not.toHaveTextContent('(install.test_game_unavailable)');
    expect(operations).toHaveTextContent("The node's agent restarted or lost the operation before it ended.");
    // A code without a translation: the agent's own words.
    expect(within(operations).getByText('Something new went wrong.')).toHaveAttribute('title', 'install.something_new');
    expect(screen.getAllByText('Install failed').length).toBeGreaterThan(0);
  });

  it('keeps showing the server when a refresh fails', async () => {
    let fail = false;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${running.id}`]: () => (fail ? problem(500, 'unexpected') : json(details(running))),
    });
    const before = connections.length;
    renderAt(`/servers/${running.id}?tab=overview`);
    const live = await openedConnection(before);
    expect(await screen.findByRole('heading', { name: 'Surf' })).toBeInTheDocument();

    fail = true;
    live.emit('ServersChanged', [running.id]);

    expect(
      await screen.findByText(/This page could not be refreshed and shows what was loaded last/),
    ).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Surf' })).toBeInTheDocument();
    expect(screen.getByRole('tablist', { name: 'Server sections' })).toBeInTheDocument();
  });

  it.each([
    ['deleted meanwhile', 404, 'server.not_found'],
    ['no longer allowed', 403, 'auth.forbidden'],
  ])('says the server is gone when a refresh finds it %s', async (_, status, code) => {
    let gone = false;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${running.id}`]: () => (gone ? problem(status, code) : json(details(running))),
    });
    const before = connections.length;
    renderAt(`/servers/${running.id}?tab=overview`);
    const live = await openedConnection(before);
    expect(await screen.findByRole('heading', { name: 'Surf' })).toBeInTheDocument();

    gone = true;
    live.emit('ServersChanged', [running.id]);

    expect(await screen.findByRole('heading', { name: 'This server does not exist.' })).toBeInTheDocument();
    expect(screen.queryByRole('tablist', { name: 'Server sections' })).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Server actions' })).not.toBeInTheDocument();
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
