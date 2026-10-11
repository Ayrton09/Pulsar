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
import { answerNext, connections, openedConnection, refuseNext } from '../../test/fakeSignalR';
import { terminals } from '../../test/fakeXterm';
import { sessionKey } from '../auth/api';
import { serverKeys } from '../servers/api';
import type { ConsoleEntry } from './types';

const operator = {
  ...adminUser,
  permissions: [
    ...adminUser.permissions,
    'node.view',
    'server.view',
    'server.console.view',
    'server.console.write',
    'server.start',
  ],
};

const server: Schemas['ServerSummary'] = {
  id: '0192f1a0-0000-7000-8000-0000000000e2',
  shortId: 'k3x7m2qa',
  name: 'Surf',
  nodeId: '0192f1a0-0000-7000-8000-0000000000a1',
  nodeName: 'Buenos Aires 1',
  nodeConnected: true,
  templateId: '0192f1a0-0000-7000-8000-0000000000f1',
  templateName: 'Pulsar test server',
  installState: 'installed',
  runtimeState: 'running',
  desiredState: 'running',
  lastStateReason: 'ready',
  lastExitCode: null,
  lastStartedAt: '2026-10-02T12:05:00Z',
  lastStoppedAt: null,
  activeOperation: null,
  createdAt: '2026-10-02T12:00:00Z',
};

const details: Schemas['ServerDetails'] = {
  server,
  description: null,
  autoStart: false,
  restartOnCrash: true,
  templateKey: 'pulsar-test',
  templateVersion: '1.0.0',
  canUpdate: false,
  ports: [],
  recentOperations: [],
  limits: { cpuPercent: null, memoryMb: null, databases: 2 },
  hasAddons: false,
};

const entry = (sequence: number, text: string, isGap = false): ConsoleEntry => ({
  sequence,
  at: '2026-10-02T12:06:00Z',
  text,
  isGap,
});

/** Renders the app at <paramref name="path"/> and returns the live connection it opens once signed in. */
async function renderAt(path: string) {
  const before = connections.length;
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<App router={router} queryClient={queryClient} />);
  return openedConnection(before);
}

function serverApi(user = operator, current = details) {
  return fakeApi({
    'GET /api/v1/auth/session': session(user),
    'GET /api/v1/servers/0192f1a0-0000-7000-8000-0000000000e2': () => json(current),
    'GET /api/v1/servers': () => json([current.server]),
    'POST /api/v1/servers/0192f1a0-0000-7000-8000-0000000000e2/console': () =>
      json({ status: 'accepted', errorCode: null }),
  });
}

const consoleText = () => terminals.at(-1)?.text ?? '';

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('server console', () => {
  it('writes what the panel holds, then pushed output, each sequence once and in order', async () => {
    serverApi();
    let answer: (snapshot: unknown) => void = () => {};
    answerNext({ WatchConsole: () => new Promise((resolve) => (answer = resolve)) });
    const live = await renderAt(`/servers/${server.id}?tab=console`);

    await waitFor(() => expect(live.calls('WatchConsole')).toHaveLength(1));
    expect(live.calls('WatchConsole')[0]?.args).toEqual([server.id, null]);
    // Pushed before the answer arrived: written after what the answer holds, without the repeat.
    live.emit('ConsoleOutput', { serverId: server.id, entries: [entry(3, 'three\r\n'), entry(4, 'four\r\n')] });
    live.emit('ConsoleOutput', { serverId: 'another-server', entries: [entry(9, 'not this one\r\n')] });
    answer({ entries: [entry(1, 'one\r\n'), entry(2, 'two\r\n'), entry(3, 'three\r\n')], nodeConnected: true });

    await waitFor(() => expect(consoleText()).toBe('one\r\ntwo\r\nthree\r\nfour\r\n'));
    live.emit('ConsoleOutput', { serverId: server.id, entries: [entry(4, 'four\r\n'), entry(5, 'five\r\n')] });
    expect(consoleText()).toBe('one\r\ntwo\r\nthree\r\nfour\r\nfive\r\n');
  });

  it('marks output the node no longer had', async () => {
    serverApi();
    answerNext({
      WatchConsole: () => ({ entries: [entry(41, '', true), entry(42, 'later\r\n')], nodeConnected: true }),
    });
    await renderAt(`/servers/${server.id}?tab=console`);

    await waitFor(() => expect(consoleText()).toContain('some output was lost here'));
    expect(consoleText()).toMatch(/lost here.*later/s);
  });

  it('after a reconnect asks only for what came after the last entry it wrote', async () => {
    serverApi();
    answerNext({ WatchConsole: () => ({ entries: [entry(7, 'seven\r\n')], nodeConnected: true }) });
    const live = await renderAt(`/servers/${server.id}?tab=console`);
    await waitFor(() => expect(consoleText()).toBe('seven\r\n'));

    live.drop();
    expect(await screen.findByText(/Live updates stopped; reconnecting/)).toBeInTheDocument();
    live.answers.WatchConsole = () => ({ entries: [entry(8, 'eight\r\n')], nodeConnected: true });
    live.reconnect();

    await waitFor(() => expect(consoleText()).toBe('seven\r\neight\r\n'));
    expect(live.calls('WatchConsole').at(-1)?.args).toEqual([server.id, 7]);
    expect(screen.queryByText(/Live updates stopped; reconnecting/)).not.toBeInTheDocument();
  });

  it('sends commands and recalls them with the arrow keys', async () => {
    const api = serverApi();
    await renderAt(`/servers/${server.id}?tab=console`);
    const user = userEvent.setup();
    const field = await screen.findByRole('textbox', { name: 'Command' });

    await user.type(field, 'say hello{Enter}');

    await waitFor(() => expect(field).toHaveValue(''));
    const sent = api.mock.calls.map(([input]) => input as Request).find((request) => request.url.endsWith('/console'));
    expect(await sent?.json()).toEqual({ command: 'say hello' });
    await user.type(field, '{ArrowUp}');
    expect(field).toHaveValue('say hello');
  });

  it('is read-only without the permission to type, and absent without the one to watch', async () => {
    serverApi({ ...operator, permissions: ['server.view', 'server.console.view'] });
    await renderAt(`/servers/${server.id}?tab=console`);
    expect(await screen.findByTestId('console-output')).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: 'Command' })).not.toBeInTheDocument();
  });

  it('has no tab for users who may not watch it', async () => {
    serverApi({ ...operator, permissions: ['server.view'] });
    await renderAt(`/servers/${server.id}?tab=console`);
    const tabs = await screen.findByRole('tablist', { name: 'Server sections' });
    expect(within(tabs).queryByRole('tab', { name: 'Console' })).not.toBeInTheDocument();
    expect(screen.queryByTestId('console-output')).not.toBeInTheDocument();
  });
});

describe('live updates', () => {
  it('shows the usage the agent pushes in the server header', async () => {
    serverApi();
    const live = await renderAt(`/servers/${server.id}`);
    await waitFor(() => expect(live.calls('WatchServer')).toHaveLength(1));

    live.emit('ServerMetrics', {
      serverId: server.id,
      at: '2026-10-02T12:06:00Z',
      cpuPercent: 12.5,
      memoryBytes: 512 * 1024 * 1024,
      processCount: 2,
      uptimeSeconds: 3_900,
    });

    const header = await screen.findByLabelText('Usage');
    expect(header).toHaveTextContent('12.5 %');
    expect(header).toHaveTextContent('512 MB');
    expect(header).toHaveTextContent('1 h 5 min');
  });

  it('shows who is playing, as plain text', async () => {
    serverApi();
    const live = await renderAt(`/servers/${server.id}`);
    await waitFor(() => expect(live.calls('WatchServer')).toHaveLength(1));

    live.emit('ServerMetrics', {
      serverId: server.id,
      at: '2026-10-02T12:06:00Z',
      cpuPercent: 3,
      memoryBytes: 1024 * 1024 * 1024,
      processCount: 1,
      uptimeSeconds: 600,
      query: {
        at: '2026-10-02T12:06:00Z',
        name: 'Retakes <b>24/7</b>',
        map: 'de_dust2',
        players: 3,
        maxPlayers: 16,
        bots: 1,
        version: '1.40.2.1',
        playerList: [
          { name: '<img src=x onerror=alert(1)>', score: 7, connectedSeconds: 3_660 },
          { name: 'Bob', score: null, connectedSeconds: null },
        ],
      },
    });

    const header = await screen.findByLabelText('Usage');
    expect(header).toHaveTextContent('3 / 16 (1 bot)');
    expect(header).toHaveTextContent('de_dust2');
    const players = await screen.findByRole('table', { name: 'Players' });
    expect(players).toHaveTextContent('<img src=x onerror=alert(1)>');
    expect(players).toHaveTextContent('1 h 1 min');
    expect(within(players).getAllByRole('row')).toHaveLength(3);
    expect(screen.getByText('Retakes <b>24/7</b> · version 1.40.2.1')).toBeInTheDocument();
    expect(document.querySelector('img, b')).toBeNull();
  });

  it('says so when the game counts its players without listing them', async () => {
    serverApi();
    const live = await renderAt(`/servers/${server.id}`);
    await waitFor(() => expect(live.calls('WatchServer')).toHaveLength(1));

    live.emit('ServerMetrics', {
      serverId: server.id,
      at: '2026-10-02T12:06:00Z',
      cpuPercent: 3,
      memoryBytes: 1024,
      processCount: 1,
      uptimeSeconds: 600,
      query: {
        at: '2026-10-02T12:06:00Z',
        name: null,
        map: null,
        players: 2,
        maxPlayers: 10,
        bots: 0,
        version: null,
        playerList: [],
      },
    });

    expect(await screen.findByText('The game does not list its players.')).toBeInTheDocument();
    expect(screen.getByLabelText('Usage')).toHaveTextContent('2 / 10');
  });

  it('fetches the servers again when the panel says they changed, instead of polling', async () => {
    const api = serverApi();
    const live = await renderAt('/servers');
    await screen.findByRole('table', { name: 'Servers' });
    await waitFor(() => expect(live.calls('WatchServers')).toHaveLength(1));
    const fetches = () => api.mock.calls.filter(([input]) => (input as Request).url.endsWith('/api/v1/servers')).length;
    const before = fetches();

    live.emit('ServersChanged', [server.id]);

    await waitFor(() => expect(fetches()).toBe(before + 1));
  });

  it('after a reconnect fetches again what is shown, but not the server settings an admin may be editing', async () => {
    serverApi();
    const before = connections.length;
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<App router={createMemoryRouter(routes, { initialEntries: ['/servers'] })} queryClient={queryClient} />);
    const live = await openedConnection(before);
    await screen.findByRole('table', { name: 'Servers' });
    queryClient.setQueryData(serverKeys.settings(server.id), { variables: [] });
    queryClient.setQueryData(serverKeys.templates, []);

    live.drop();
    live.reconnect();

    await waitFor(() => expect(queryClient.getQueryState(serverKeys.templates)?.isInvalidated).toBe(true));
    expect(queryClient.getQueryState(serverKeys.settings(server.id))?.isInvalidated).toBe(false);
  });

  it('writes operation progress in place', async () => {
    const operation: Schemas['OperationView'] = {
      id: '0192f1a0-0000-7000-8000-0000000000b1',
      type: 'serverUpdate',
      status: 'running',
      stage: 'downloading',
      percent: 10,
      errorCode: null,
      errorMessage: null,
      createdAt: '2026-10-02T12:00:00Z',
      completedAt: null,
      cancellable: true,
      cancelRequested: false,
    };
    serverApi(operator, { ...details, server: { ...server, runtimeState: 'offline', activeOperation: operation } });
    const live = await renderAt(`/servers/${server.id}`);
    const progress = await screen.findByRole('progressbar');
    expect(progress).toHaveAttribute('aria-valuenow', '10');

    live.emit('OperationProgress', {
      serverId: server.id,
      operationId: operation.id,
      stage: 'downloading',
      percent: 64.5,
    });

    await waitFor(() => expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '65'));
  });
});

describe('live channel and session', () => {
  /** The servers page, with a session the test ends (`signedIn`) or breaks (`sessionDown`). */
  function sessionApi() {
    const state = { signedIn: true, sessionDown: false };
    const api = fakeApi({
      'GET /api/v1/auth/session': () =>
        state.sessionDown
          ? problem(503, 'unavailable')
          : json({ setupRequired: false, user: state.signedIn ? operator : null, insecureConnection: false }),
      'GET /api/v1/servers': () => json([server]),
    });
    const sessionReads = () =>
      api.mock.calls.filter(([input]) => (input as Request).url.endsWith('/api/v1/auth/session')).length;
    return { state, sessionReads };
  }

  async function renderServers() {
    const before = connections.length;
    const router = createMemoryRouter(routes, { initialEntries: ['/servers'] });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<App router={router} queryClient={queryClient} />);
    const live = await openedConnection(before);
    return { live, router, queryClient };
  }

  it('reads the session again while it cannot reconnect, and signs out a session that ended', async () => {
    const { state, sessionReads } = sessionApi();
    const { live, router } = await renderServers();
    await screen.findByRole('table', { name: 'Servers' });
    const before = sessionReads();

    live.drop();
    live.failReconnect();

    await waitFor(() => expect(sessionReads()).toBe(before + 2));
    expect(screen.getByRole('table', { name: 'Servers' })).toBeInTheDocument();
    // Signed out in another tab, or the cookie expired while the laptop slept.
    state.signedIn = false;
    live.failReconnect();

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/login');
  });

  it('says so when the first connection fails, and reads the session again', async () => {
    const { sessionReads } = sessionApi();
    refuseNext();
    await renderServers();
    await screen.findByRole('table', { name: 'Servers' });

    expect(await screen.findByText('Live updates paused, reconnecting…')).toBeInTheDocument();
    await waitFor(() => expect(sessionReads()).toBeGreaterThan(1));
  });

  it('keeps the page when reading the session again fails', async () => {
    const { state } = sessionApi();
    const { queryClient } = await renderServers();
    await screen.findByRole('table', { name: 'Servers' });

    state.sessionDown = true;
    await queryClient.invalidateQueries({ queryKey: sessionKey });

    expect(await screen.findByText(/Could not refresh/)).toBeInTheDocument();
    expect(screen.getByRole('table', { name: 'Servers' })).toBeInTheDocument();
    state.sessionDown = false;
    await userEvent.setup().click(screen.getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(screen.queryByText(/Could not refresh/)).not.toBeInTheDocument());
  });
});
