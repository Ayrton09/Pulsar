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
import { databaseKeys } from './api';

const serverId = '0192f1a0-0000-7000-8000-0000000000e1';
const hostId = '0192f1a0-0000-7000-8000-0000000000d1';
const manager = { ...adminUser, permissions: ['server.view', 'server.databases.view', 'server.databases.manage'] };
const viewer = { ...adminUser, permissions: ['server.view', 'server.databases.view'] };

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
    templateKey: 'rust',
    gameAddress: null,
    gameUpdateAvailable: false,
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
  hasMods: false,
};

const main: Schemas['ServerDatabaseView'] = {
  id: '0192f1a0-0000-7000-8000-0000000000c1',
  engine: 'mysql',
  name: 'sk3x7m2qa_main',
  userName: 'sk3x7m2qa_main',
  hostId,
  host: 'LAN MariaDB',
  address: '10.0.0.5',
  port: 3306,
  userHost: '%',
  createdAt: '2026-10-09T12:00:00Z',
  passwordChangedAt: '2026-10-09T12:00:00Z',
  forGame: false,
};

function list(databases: Schemas['ServerDatabaseView'][], limit = 2): Schemas['ServerDatabaseList'] {
  return { limit, databases, hosts: [{ id: hostId, name: 'LAN MariaDB', engine: 'mysql' }] };
}

function renderAt(
  path: string,
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } }),
) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  render(<App router={router} queryClient={queryClient} />);
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('databases tab', () => {
  it('lists the databases without passwords for those who only see them', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/databases`]: () => json(list([main])),
    });

    renderAt(`/servers/${serverId}?tab=databases`);

    const table = await screen.findByRole('table', { name: 'Databases' });
    expect(table).toHaveTextContent('sk3x7m2qa_main');
    expect(table).toHaveTextContent('10.0.0.5:3306');
    expect(screen.getByText('1 of 2 databases')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Show password' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Create database' })).not.toBeInTheDocument();
  });

  it('leaves where a PostgreSQL role connects from to PostgreSQL', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/databases`]: () =>
        json(list([{ ...main, engine: 'postgres', host: 'Panel PostgreSQL', port: 5432 }])),
    });

    renderAt(`/servers/${serverId}?tab=databases`);

    const table = await screen.findByRole('table', { name: 'Databases' });
    expect(table).toHaveTextContent('PostgreSQL on Panel PostgreSQL');
    expect(table).not.toHaveTextContent('connections from');
  });

  it('lets managers keep the game data in a PostgreSQL database, and back in its own files', async () => {
    const bodies: unknown[] = [];
    const pgHostId = '0192f1a0-0000-7000-8000-0000000000d2';
    const gameDatabase: Schemas['ServerDatabaseView'] = {
      ...main,
      id: '0192f1a0-0000-7000-8000-0000000000c9',
      name: 'sk3x7m2qa_game',
      userName: 'sk3x7m2qa_game',
      engine: 'postgres',
      hostId: pgHostId,
      host: 'Panel PostgreSQL',
      port: 5432,
      forGame: true,
    };
    let current: string | null = null;
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/databases`]: () =>
        json({
          limit: 2,
          databases: current ? [gameDatabase] : [],
          hosts: [
            { id: hostId, name: 'LAN MariaDB', engine: 'mysql' },
            { id: pgHostId, name: 'Panel PostgreSQL', engine: 'postgres' },
          ],
          game: { engine: 'postgres', databaseId: current, supported: true },
        }),
      [`PUT /api/v1/servers/${serverId}/databases/game`]: async (request) => {
        const body = (await request.json()) as { hostId: string | null };
        bodies.push(body);
        current = body.hostId ? gameDatabase.id : null;
        return new Response(null, { status: 204 });
      },
    });

    renderAt(`/servers/${serverId}?tab=databases`);
    const user = userEvent.setup();

    expect(await screen.findByText('The game keeps its data in its own files.')).toBeInTheDocument();
    const choice = screen.getByLabelText("Keep the game's data in");
    // Only hosts of the engine the game takes.
    expect(within(choice).queryByRole('option', { name: /LAN MariaDB/ })).not.toBeInTheDocument();
    await user.selectOptions(choice, 'PostgreSQL on Panel PostgreSQL');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(
      await screen.findByText('The game keeps its data in sk3x7m2qa_game, on Panel PostgreSQL.'),
    ).toBeInTheDocument();
    expect(screen.getByText('Used by the game')).toBeInTheDocument();
    // Its database is not counted, nor deleted while the game's.
    expect(screen.getByText('0 of 2 databases')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText("Keep the game's data in"), "The game's own files");
    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('The game keeps its data in its own files.')).toBeInTheDocument();
    expect(bodies).toEqual([{ hostId: pgHostId }, { hostId: null }]);
  });

  it('is not there without the permission to see databases', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['server.view'] }),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
    });

    renderAt(`/servers/${serverId}?tab=databases`);

    expect(await screen.findByRole('tab', { name: 'Overview', selected: true })).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Databases' })).not.toBeInTheDocument();
  });

  it('creates one and shows its connection details once, and explains a refusal', async () => {
    const bodies: unknown[] = [];
    let databases: Schemas['ServerDatabaseView'][] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/databases`]: () => json(list(databases)),
      [`POST /api/v1/servers/${serverId}/databases`]: async (request) => {
        const body = (await request.json()) as { name: string };
        bodies.push(body);
        if (body.name === 'taken') {
          return problem(409, 'database.name_taken');
        }
        databases = [main];
        return json({ database: main, password: 'Kq7mPz2xVb9nRt4wLs8yHc3d' }, 201);
      },
    });
    renderAt(`/servers/${serverId}?tab=databases`);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Create database' }));
    const dialog = screen.getByRole('dialog', { name: 'Create database' });
    const name = within(dialog).getByLabelText('Name');
    await user.type(name, 'Bad-Name');
    expect(within(dialog).getByRole('button', { name: 'Create' })).toBeDisabled();
    await user.clear(name);
    await user.type(name, 'taken');
    expect(dialog).toHaveTextContent('The database (and its user) will be sk3x7m2qa_taken.');
    await user.click(within(dialog).getByRole('button', { name: 'Create' }));
    expect(await within(dialog).findByText('The server already has a database with that name.')).toBeInTheDocument();

    await user.clear(name);
    await user.type(name, 'main');
    await user.click(within(dialog).getByRole('button', { name: 'Create' }));

    const shown = await screen.findByRole('region', { name: 'Connection details of sk3x7m2qa_main' });
    expect(shown).toHaveTextContent('Kq7mPz2xVb9nRt4wLs8yHc3d');
    expect(shown).toHaveTextContent('10.0.0.5:3306');
    expect(bodies).toEqual([
      { hostId, name: 'taken' },
      { hostId, name: 'main' },
    ]);
    // Two of two: no more.
    expect(await screen.findByRole('table', { name: 'Databases' })).toHaveTextContent('sk3x7m2qa_main');
  });

  it('rotates a password after asking, and deletes', async () => {
    const calls: string[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/databases`]: () => json(list([main], 1)),
      [`POST /api/v1/servers/${serverId}/databases/${main.id}/rotate-password`]: () => {
        calls.push('rotate');
        return json({ database: main, password: 'NewPassword234567890abcd' });
      },
      [`DELETE /api/v1/servers/${serverId}/databases/${main.id}`]: () => {
        calls.push('delete');
        return new Response(null, { status: 204 });
      },
    });
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderAt(`/servers/${serverId}?tab=databases`);
    const user = userEvent.setup();

    expect(await screen.findByText('The server has as many databases as it may have.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create database' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Change password' }));
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('sk3x7m2qa_main'));
    expect(await screen.findByRole('region', { name: 'Connection details of sk3x7m2qa_main' })).toHaveTextContent(
      'NewPassword234567890abcd',
    );
    await user.click(screen.getByRole('button', { name: 'Delete' }));
    await vi.waitFor(() => expect(calls).toEqual(['rotate', 'delete']));
    // The deleted database's password is not left on screen.
    await vi.waitFor(() =>
      expect(screen.queryByRole('region', { name: 'Connection details of sk3x7m2qa_main' })).not.toBeInTheDocument(),
    );
  });

  it('keeps a shown password and the list when fetching them again fails', async () => {
    let down = false;
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/databases`]: () => (down ? problem(503, 'unavailable') : json(list([main]))),
      [`GET /api/v1/servers/${serverId}/databases/${main.id}/password`]: () =>
        json({ database: main, password: 'Kq7mPz2xVb9nRt4wLs8yHc3d' }),
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
    renderAt(`/servers/${serverId}?tab=databases`, queryClient);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Show password' }));
    const shown = await screen.findByRole('region', { name: 'Connection details of sk3x7m2qa_main' });
    // A reconnect refetches, and the panel does not answer.
    down = true;
    await queryClient.invalidateQueries({ queryKey: databaseKeys.server(serverId) });

    expect(await screen.findByText(/Could not refresh/)).toBeInTheDocument();
    expect(shown).toBeInTheDocument();
    expect(shown).toHaveTextContent('Kq7mPz2xVb9nRt4wLs8yHc3d');
    expect(screen.getByRole('table', { name: 'Databases' })).toHaveTextContent('sk3x7m2qa_main');

    down = false;
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    await vi.waitFor(() => expect(screen.queryByText(/Could not refresh/)).not.toBeInTheDocument());
  });
});

describe('database hosts page', () => {
  it('adds a host, sending the password only when given', async () => {
    const bodies: unknown[] = [];
    let hosts: Schemas['DatabaseHostView'][] = [];
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['settings.manage'] }),
      'GET /api/v1/nodes': () => json([]),
      'GET /api/v1/database-hosts': () => json({ hosts }),
      'POST /api/v1/database-hosts': async (request) => {
        bodies.push(await request.json());
        hosts = [
          {
            id: hostId,
            engine: 'mysql',
            name: 'LAN MariaDB',
            host: '10.0.0.5',
            port: 3306,
            userName: 'pulsar',
            serverAddress: null,
            serverPort: null,
            userHost: '%',
            nodeIds: [],
            requireTls: false,
            databases: 0,
            createdAt: '2026-10-09T12:00:00Z',
            updatedAt: '2026-10-09T12:00:00Z',
          },
        ];
        return json(hosts[0], 201);
      },
      [`POST /api/v1/database-hosts/${hostId}/test`]: () => json({ version: '12.3.3-MariaDB' }),
    });
    renderAt('/admin/database-hosts');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Add database host' }));
    const dialog = screen.getByRole('dialog', { name: 'Add database host' });
    await user.type(within(dialog).getByLabelText('Name'), 'LAN MariaDB');
    await user.type(within(dialog).getByLabelText('Address'), '10.0.0.5');
    await user.type(within(dialog).getByLabelText('User'), 'pulsar');
    await user.type(within(dialog).getByLabelText('Password'), 'host password');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    const table = await screen.findByRole('table', { name: 'Database hosts' });
    expect(table).toHaveTextContent('pulsar@10.0.0.5:3306');
    expect(table).toHaveTextContent('Every node');
    expect(bodies).toEqual([
      {
        name: 'LAN MariaDB',
        host: '10.0.0.5',
        port: 3306,
        userName: 'pulsar',
        password: 'host password',
        serverAddress: null,
        serverPort: null,
        userHost: '%',
        nodeIds: [],
        requireTls: null,
        engine: 'mysql',
      },
    ]);

    await user.click(within(table).getByRole('button', { name: 'Test' }));
    expect(await screen.findByText('Connected: 12.3.3-MariaDB.')).toBeInTheDocument();
  });

  it('adds a PostgreSQL host on its own port, without the connections-from setting of MySQL', async () => {
    const bodies: unknown[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['settings.manage'] }),
      'GET /api/v1/nodes': () => json([]),
      'GET /api/v1/database-hosts': () => json({ hosts: [] }),
      'POST /api/v1/database-hosts': async (request) => {
        bodies.push(await request.json());
        return problem(502, 'database_host.unreachable', 'refused');
      },
    });
    renderAt('/admin/database-hosts');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Add database host' }));
    const dialog = screen.getByRole('dialog', { name: 'Add database host' });
    expect(within(dialog).getByLabelText('Port')).toHaveValue(3306);
    expect(within(dialog).getByLabelText('Connections from')).toBeInTheDocument();
    await user.selectOptions(within(dialog).getByLabelText('Runs'), 'postgres');
    expect(within(dialog).getByLabelText('Port')).toHaveValue(5432);
    expect(within(dialog).queryByLabelText('Connections from')).not.toBeInTheDocument();
    expect(within(dialog).getByText(/CREATEDB and CREATEROLE/)).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText('Name'), 'Panel PostgreSQL');
    await user.type(within(dialog).getByLabelText('Address'), '127.0.0.1');
    await user.type(within(dialog).getByLabelText('User'), 'pulsar_games');
    await user.type(within(dialog).getByLabelText('Password'), 'host password');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toMatchObject({ engine: 'postgres', port: 5432, userHost: null, host: '127.0.0.1' });
  });

  it('requires TLS by default outside the local network, and sends a choice made', async () => {
    const bodies: unknown[] = [];
    const host: Schemas['DatabaseHostView'] = {
      id: hostId,
      engine: 'mysql',
      name: 'Cloud MySQL',
      host: 'db.example.com',
      port: 3306,
      userName: 'pulsar',
      serverAddress: null,
      serverPort: null,
      userHost: '%',
      nodeIds: [],
      requireTls: true,
      databases: 0,
      createdAt: '2026-10-09T12:00:00Z',
      updatedAt: '2026-10-09T12:00:00Z',
    };
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['settings.manage'] }),
      'GET /api/v1/nodes': () => json([]),
      'GET /api/v1/database-hosts': () => json({ hosts: [host] }),
      [`PUT /api/v1/database-hosts/${hostId}`]: async (request) => {
        bodies.push(await request.json());
        return json(host);
      },
    });
    renderAt('/admin/database-hosts');
    const user = userEvent.setup();

    expect(await screen.findByRole('table', { name: 'Database hosts' })).toHaveTextContent('TLS required');
    await user.click(screen.getByRole('button', { name: 'Add database host' }));
    let dialog = screen.getByRole('dialog', { name: 'Add database host' });
    const tls = within(dialog).getByRole('checkbox', { name: 'Require TLS' });
    expect(tls).not.toBeChecked();
    await user.type(within(dialog).getByLabelText('Address'), 'db.example.com');
    expect(tls).toBeChecked();
    await user.clear(within(dialog).getByLabelText('Address'));
    await user.type(within(dialog).getByLabelText('Address'), '192.168.1.20');
    expect(tls).not.toBeChecked();
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    await user.click(screen.getByRole('button', { name: 'Edit' }));
    dialog = screen.getByRole('dialog', { name: 'Edit database host' });
    expect(within(dialog).getByRole('checkbox', { name: 'Require TLS' })).toBeChecked();
    await user.click(within(dialog).getByRole('checkbox', { name: 'Require TLS' }));
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    await vi.waitFor(() => expect(bodies).toMatchObject([{ host: 'db.example.com', requireTls: false }]));
  });

  it('recomputes the TLS default when the address of an edited host moves outside the local network, until the box is touched', async () => {
    const bodies: unknown[] = [];
    const host: Schemas['DatabaseHostView'] = {
      id: hostId,
      engine: 'mysql',
      name: 'LAN MariaDB',
      host: '10.0.0.5',
      port: 3306,
      userName: 'pulsar',
      serverAddress: null,
      serverPort: null,
      userHost: '%',
      nodeIds: [],
      requireTls: false,
      databases: 0,
      createdAt: '2026-10-09T12:00:00Z',
      updatedAt: '2026-10-09T12:00:00Z',
    };
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['settings.manage'] }),
      'GET /api/v1/nodes': () => json([]),
      'GET /api/v1/database-hosts': () => json({ hosts: [host] }),
      [`PUT /api/v1/database-hosts/${hostId}`]: async (request) => {
        bodies.push(await request.json());
        return json(host);
      },
    });
    renderAt('/admin/database-hosts');
    const user = userEvent.setup();

    await screen.findByRole('table', { name: 'Database hosts' });
    await user.click(screen.getByRole('button', { name: 'Edit' }));
    const dialog = screen.getByRole('dialog', { name: 'Edit database host' });
    const tls = within(dialog).getByRole('checkbox', { name: 'Require TLS' });
    expect(tls).not.toBeChecked();

    // Untouched: it follows the address, as when adding.
    await user.clear(within(dialog).getByLabelText('Address'));
    await user.type(within(dialog).getByLabelText('Address'), 'db.example.com');
    expect(tls).toBeChecked();

    // A choice made is kept.
    await user.click(tls);
    await user.clear(within(dialog).getByLabelText('Address'));
    await user.type(within(dialog).getByLabelText('Address'), 'other.example.com');
    expect(tls).not.toBeChecked();
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    await vi.waitFor(() => expect(bodies).toMatchObject([{ host: 'other.example.com', requireTls: false }]));
  });

  it('says when the nodes cannot be listed, and what the host then applies to', async () => {
    const host: Schemas['DatabaseHostView'] = {
      id: hostId,
      engine: 'mysql',
      name: 'LAN MariaDB',
      host: '10.0.0.5',
      port: 3306,
      userName: 'pulsar',
      serverAddress: null,
      serverPort: null,
      userHost: '%',
      nodeIds: ['0192f1a0-0000-7000-8000-0000000000a1', '0192f1a0-0000-7000-8000-0000000000a2'],
      requireTls: false,
      databases: 0,
      createdAt: '2026-10-09T12:00:00Z',
      updatedAt: '2026-10-09T12:00:00Z',
    };
    fakeApi({
      // Settings only: listing the nodes takes node.view, node.manage or server.create.
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['settings.manage'] }),
      'GET /api/v1/nodes': () => problem(403, 'auth.forbidden'),
      'GET /api/v1/database-hosts': () => json({ hosts: [host] }),
    });
    renderAt('/admin/database-hosts');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Add database host' }));
    let dialog = screen.getByRole('dialog', { name: 'Add database host' });
    expect(await within(dialog).findByText(/The nodes cannot be listed:/)).toBeInTheDocument();
    expect(
      within(dialog).getByText('With no node ticked, servers of every node may use this host.'),
    ).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    await user.click(screen.getByRole('button', { name: 'Edit' }));
    dialog = screen.getByRole('dialog', { name: 'Edit database host' });
    expect(await within(dialog).findByText('The host stays limited to the 2 nodes it has.')).toBeInTheDocument();
  });
});
