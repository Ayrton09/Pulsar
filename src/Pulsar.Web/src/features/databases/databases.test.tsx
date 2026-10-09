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

const main: Schemas['ServerDatabaseView'] = {
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

function list(databases: Schemas['ServerDatabaseView'][], limit = 2): Schemas['ServerDatabaseList'] {
  return { limit, databases, hosts: [{ id: hostId, name: 'LAN MariaDB' }] };
}

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
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
      },
    ]);

    await user.click(within(table).getByRole('button', { name: 'Test' }));
    expect(await screen.findByText('Connected: 12.3.3-MariaDB.')).toBeInTheDocument();
  });

  it('requires TLS by default outside the local network, and sends a choice made', async () => {
    const bodies: unknown[] = [];
    const host: Schemas['DatabaseHostView'] = {
      id: hostId,
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
});
