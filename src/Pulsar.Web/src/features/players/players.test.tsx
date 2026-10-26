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
import { connections, openedConnection } from '../../test/fakeSignalR';

const serverId = '0192f1a0-0000-7000-8000-0000000000e8';
const moderator = { ...adminUser, permissions: ['server.view', 'server.players.manage'] };
const operator = { ...adminUser, permissions: ['server.view', 'server.players.manage', 'server.console.write'] };
const viewer = { ...adminUser, permissions: ['server.view', 'server.console.write'] };

const server: Schemas['ServerSummary'] = {
  id: serverId,
  shortId: 'p4x7m2qa',
  name: 'Creative',
  nodeId: '0192f1a0-0000-7000-8000-0000000000a1',
  nodeName: 'Buenos Aires 1',
  nodeConnected: true,
  templateId: '0192f1a0-0000-7000-8000-0000000000f1',
  templateName: 'Minecraft: Java Edition',
  templateKey: 'minecraft',
  gameAddress: null,
  gameUpdateAvailable: false,
  installState: 'installed',
  runtimeState: 'running',
  desiredState: 'running',
  lastStateReason: null,
  lastExitCode: null,
  lastStartedAt: null,
  lastStoppedAt: null,
  activeOperation: null,
  createdAt: '2026-10-02T12:00:00Z',
};

const minecraftPlayers: Schemas['ServerPlayersView'] = {
  byId: false,
  spaces: true,
  actions: ['kick', 'ban', 'unban', 'whitelistAdd', 'whitelistRemove', 'adminAdd', 'adminRemove'],
  lists: [],
};

const details: Schemas['ServerDetails'] = {
  server,
  description: null,
  autoStart: false,
  restartOnCrash: true,
  templateKey: 'minecraft',
  templateVersion: '1.5.0',
  canUpdate: true,
  ports: [],
  recentOperations: [],
  limits: { cpuPercent: null, memoryMb: null, databases: 2 },
  hasAddons: false,
  hasMods: false,
  players: minecraftPlayers,
};

async function renderAt(path: string) {
  const before = connections.length;
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
  render(<App router={router} queryClient={queryClient} />);
  return openedConnection(before);
}

/** Records what the card sends; the game takes it. */
function playersApi(
  user: Schemas['CurrentUserResponse'],
  current: Schemas['ServerDetails'],
  lists?: Schemas['ServerPlayerLists'],
) {
  const sent: unknown[] = [];
  fakeApi({
    'GET /api/v1/auth/session': session(user),
    [`GET /api/v1/servers/${serverId}`]: () => json(current),
    [`GET /api/v1/servers/${serverId}/game-updates`]: () => json(null, 404),
    [`GET /api/v1/servers/${serverId}/players/lists`]: () => json(lists ?? { lists: [] }),
    [`POST /api/v1/servers/${serverId}/players`]: async (request) => {
      sent.push(await request.json());
      return json({ status: 'accepted', errorCode: null });
    },
  });
  return sent;
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('players', () => {
  it('kicks and bans who plays, and offers nothing for a name the console would read as more', async () => {
    const sent = playersApi(moderator, details);
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const live = await renderAt(`/servers/${serverId}`);
    await waitFor(() => expect(live.calls('WatchServer')).toHaveLength(1));

    live.emit('ServerMetrics', {
      serverId,
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
        playerList: [
          { name: 'Big Bob', score: null, connectedSeconds: null },
          { name: '@a', score: null, connectedSeconds: null },
          { name: ' Eve', score: null, connectedSeconds: null },
        ],
      },
    });

    const table = await screen.findByRole('table', { name: 'Players' });
    const bob = within(table).getByRole('row', { name: /^Big Bob/ });
    // Making an admin needs the console too: a moderator is not offered it.
    expect(within(bob).queryByRole('button', { name: 'Make Big Bob admin' })).not.toBeInTheDocument();
    expect(within(within(table).getByRole('row', { name: /^@a/ })).queryAllByRole('button')).toHaveLength(0);
    // Sent without the space, it could be someone else's name.
    expect(within(within(table).getByRole('row', { name: /Eve/ })).queryAllByRole('button')).toHaveLength(0);

    await userEvent.click(within(bob).getByRole('button', { name: 'Kick Big Bob' }));
    await waitFor(() => expect(sent).toEqual([{ action: 'kick', player: 'Big Bob' }]));
    expect(await screen.findByText('Sent to the game: its answer is in the console.')).toBeInTheDocument();
    expect(confirm).not.toHaveBeenCalled();

    await userEvent.click(within(bob).getByRole('button', { name: 'Ban Big Bob' }));
    await waitFor(() => expect(sent).toHaveLength(2));
    expect(sent[1]).toEqual({ action: 'ban', player: 'Big Bob' });
    expect(confirm).toHaveBeenCalledWith('Ban Big Bob? They cannot join again until unbanned.');
  });

  it("acts on any player by name and shows the game's lists, with a way off them", async () => {
    const sent = playersApi(
      operator,
      { ...details, players: { ...minecraftPlayers, lists: ['banned', 'whitelist', 'admins'] } },
      {
        lists: [
          { kind: 'banned', players: ['Alice', 'a"b'], truncated: false, errorCode: null },
          { kind: 'whitelist', players: [], truncated: false, errorCode: null },
          { kind: 'admins', players: [], truncated: false, errorCode: 'players.list_unreadable' },
        ],
      },
    );
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    await renderAt(`/servers/${serverId}`);

    const lists = await screen.findByRole('region', { name: "The game's lists" });
    expect(await within(lists).findByText('Alice')).toBeInTheDocument();
    expect(within(lists).getByText('Nobody.')).toBeInTheDocument();
    expect(within(lists).getByText("The game's file for this list could not be read.")).toBeInTheDocument();
    // The game wrote it: shown, but only a name the panel would send gets a button.
    expect(within(lists).getByText('a"b')).toBeInTheDocument();
    expect(within(lists).getAllByRole('button')).toHaveLength(1);

    await userEvent.click(within(lists).getByRole('button', { name: 'Unban Alice' }));
    await waitFor(() => expect(sent).toEqual([{ action: 'unban', player: 'Alice' }]));

    const name = screen.getByRole('textbox', { name: 'Player name' });
    await userEvent.type(name, 'Bob;stop');
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
    expect(screen.getByText('Letters, digits, single spaces and _ - . only, up to 64.')).toBeInTheDocument();
    await userEvent.clear(name);
    await userEvent.type(name, ' Notch ');
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Action' }), 'whitelistAdd');
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() => expect(sent).toHaveLength(2));
    expect(sent[1]).toEqual({ action: 'whitelistAdd', player: 'Notch' });
  });

  it('waits for the server to run before it sends anything', async () => {
    playersApi(operator, { ...details, server: { ...server, runtimeState: 'offline', desiredState: 'stopped' } });
    await renderAt(`/servers/${serverId}`);

    expect(await screen.findByText('Start the server to send commands to its players.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
  });

  it('is not shown to who may not manage players', async () => {
    playersApi(viewer, { ...details, server: { ...server, runtimeState: 'offline', desiredState: 'stopped' } });
    await renderAt(`/servers/${serverId}`);

    expect(await screen.findByRole('heading', { name: 'Creative' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Players' })).not.toBeInTheDocument();
  });
});
