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

const nodeId = '0192f1a0-0000-7000-8000-0000000000a1';
const templateId = '0192f1a0-0000-7000-8000-0000000000f2';
const serverId = '0192f1a0-0000-7000-8000-0000000000e9';
const allocationA = '0192f1a0-0000-7000-8000-0000000000c1';
const allocationB = '0192f1a0-0000-7000-8000-0000000000c2';
const allocationHidden = '0192f1a0-0000-7000-8000-0000000000c4';

const operator = {
  ...adminUser,
  permissions: [
    ...adminUser.permissions,
    'node.view',
    'node.manage',
    'template.view',
    'template.manage',
    'server.view',
    'server.create',
    'server.start',
    'server.stop',
    'server.install',
    'server.startup.edit',
    'server.settings.edit',
  ],
};

const template: Schemas['TemplateView'] = {
  id: templateId,
  key: 'css',
  version: '1.0.0',
  name: 'Counter-Strike: Source',
  description: null,
  isTestGame: false,
  source: 'bundled',
};

const variable = (overrides: Partial<Schemas['TemplateVariable']>): Schemas['TemplateVariable'] => ({
  key: 'x',
  label: 'X',
  type: 'string',
  default: null,
  required: false,
  secret: false,
  userEditable: true,
  pattern: null,
  maxLength: null,
  min: null,
  max: null,
  options: [],
  description: null,
  ...overrides,
});

const hostname = variable({
  key: 'hostname',
  label: 'Server name',
  default: 'Pulsar CS:S',
  pattern: '^[^";]{1,64}$',
  maxLength: 64,
});
const maxPlayers = variable({ key: 'maxPlayers', label: 'Slots', type: 'integer', default: '16', min: 2, max: 64 });
const mode = variable({
  key: 'mode',
  label: 'Mode',
  type: 'enum',
  default: 'casual',
  required: true,
  options: [
    { value: 'casual', label: 'Casual' },
    { value: 'competitive', label: 'Competitive' },
  ],
});
const password = variable({ key: 'password', label: 'Join password', secret: true, pattern: '^[A-Za-z0-9_-]{1,32}$' });
const variables = [hostname, maxPlayers, mode, password];

const templateDetails: Schemas['TemplateDetails'] = {
  template,
  minDiskGb: 4,
  minMemoryMb: 1024,
  variables,
  ports: [
    { name: 'game', label: 'Game', protocols: ['udp'], default: 27015, required: true, description: null },
    { name: 'tv', label: 'SourceTV', protocols: ['udp'], default: 27020, required: false, description: null },
  ],
  allowExtraArguments: true,
  canUpdate: true,
  capabilities: {
    executable: 'srcds.exe',
    steamAppIds: [232330],
    configFiles: ['cstrike/cfg/server.cfg'],
    consoleMode: 'logfile+rcon',
  },
};

const allocations: Schemas['AllocationView'][] = [
  {
    id: allocationA,
    ipAddress: '0.0.0.0',
    port: 27015,
    serverId: null,
    serverName: null,
    portName: null,
    notes: null,
    inUse: false,
  },
  {
    id: allocationB,
    ipAddress: '0.0.0.0',
    port: 27020,
    serverId: null,
    serverName: null,
    portName: null,
    notes: null,
    inUse: false,
  },
  // Held by a server the user cannot see: taken, without its holder.
  {
    id: allocationHidden,
    ipAddress: '0.0.0.0',
    port: 27025,
    serverId: null,
    serverName: null,
    portName: null,
    notes: null,
    inUse: true,
  },
];

const summary: Schemas['ServerSummary'] = {
  id: serverId,
  shortId: 'k3x7m2qa',
  name: 'Retakes',
  nodeId,
  nodeName: 'Buenos Aires 1',
  nodeConnected: true,
  templateId,
  templateName: 'Counter-Strike: Source',
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

function details(server: Schemas['ServerSummary'] = summary): Schemas['ServerDetails'] {
  return {
    server,
    description: null,
    autoStart: false,
    restartOnCrash: true,
    templateKey: 'css',
    templateVersion: '1.0.0',
    canUpdate: true,
    ports: [
      { name: 'game', label: 'Game', required: true, allocationId: allocationA, ipAddress: '0.0.0.0', port: 27015 },
      { name: 'tv', label: 'SourceTV', required: false, allocationId: null, ipAddress: null, port: null },
    ],
    recentOperations: [],
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

describe('creating a game server', () => {
  it('asks for the game settings and ports, checking values before sending them', async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/servers': () => json([]),
      'GET /api/v1/nodes': () => json([{ id: nodeId, name: 'Buenos Aires 1', connected: true, status: 'active' }]),
      'GET /api/v1/templates': () => json([template]),
      [`GET /api/v1/templates/${templateId}`]: () => json(templateDetails),
      [`GET /api/v1/nodes/${nodeId}/allocations`]: () => json(allocations),
      'POST /api/v1/servers': async (request) => {
        body = await request.json();
        return json(details({ ...summary, installState: 'installing' }), 201);
      },
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
    });
    renderAt('/servers');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Create server' }));
    await user.type(screen.getByLabelText('Name'), 'Retakes');
    await user.selectOptions(screen.getByLabelText('Node'), 'Buenos Aires 1');
    await user.selectOptions(screen.getByLabelText('Game'), 'Counter-Strike: Source 1.0.0');
    expect(await screen.findByText(/about 4 GB of disk and 1 GB of memory/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Next' }));

    const slots = await screen.findByLabelText('Slots');
    expect(slots).toHaveValue(16);
    await user.clear(slots);
    await user.type(slots, '99');
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(screen.getByText('Out of the allowed range.')).toBeInTheDocument();

    await user.clear(slots);
    await user.type(slots, '24');
    // Its pattern is the panel's to check (without backtracking), when saving.
    await user.type(screen.getByLabelText('Join password'), 'join-me');
    await user.selectOptions(screen.getByLabelText('Mode *'), 'Competitive');
    await user.click(screen.getByRole('button', { name: 'Next' }));

    const game = await screen.findByLabelText('Game *');
    expect(within(game).getByRole('option', { name: /Automatic/ })).toBeInTheDocument();
    expect(within(game).queryByRole('option', { name: '0.0.0.0:27025' })).not.toBeInTheDocument();
    await user.selectOptions(screen.getByLabelText('SourceTV'), '0.0.0.0:27020');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByRole('heading', { name: 'Retakes' })).toBeInTheDocument();
    // Only what differs from the defaults; the game port is left to the panel.
    expect(body).toEqual({
      name: 'Retakes',
      description: null,
      nodeId,
      templateId,
      variables: { maxPlayers: '24', mode: 'competitive', password: 'join-me' },
      ports: { tv: allocationB },
    });
  });
});

describe('changing the node of a server being created', () => {
  it('forgets the ports chosen on the other node, whose allocations are not this one', async () => {
    let body: unknown;
    const otherNode = '0192f1a0-0000-7000-8000-0000000000a2';
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/servers': () => json([]),
      'GET /api/v1/nodes': () =>
        json([
          { id: nodeId, name: 'Buenos Aires 1', connected: true, status: 'active' },
          { id: otherNode, name: 'Córdoba 1', connected: true, status: 'active' },
        ]),
      'GET /api/v1/templates': () => json([template]),
      [`GET /api/v1/templates/${templateId}`]: () => json(templateDetails),
      [`GET /api/v1/nodes/${nodeId}/allocations`]: () => json(allocations),
      [`GET /api/v1/nodes/${otherNode}/allocations`]: () =>
        json([{ ...allocations[1], id: '0192f1a0-0000-7000-8000-0000000000d2', ipAddress: '10.0.0.2' }]),
      'POST /api/v1/servers': async (request) => {
        body = await request.json();
        return json(details({ ...summary, installState: 'installing' }), 201);
      },
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
    });
    renderAt('/servers');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Create server' }));
    await user.type(screen.getByLabelText('Name'), 'Retakes');
    await user.selectOptions(screen.getByLabelText('Node'), 'Buenos Aires 1');
    await user.selectOptions(screen.getByLabelText('Game'), 'Counter-Strike: Source 1.0.0');
    await user.click(await screen.findByRole('button', { name: 'Next' }));
    await user.click(await screen.findByRole('button', { name: 'Next' }));
    const first = await screen.findByLabelText('SourceTV');
    await within(first).findByRole('option', { name: '0.0.0.0:27020' });
    await user.selectOptions(first, '0.0.0.0:27020');

    // Back to the first step and another node.
    await user.click(screen.getByRole('button', { name: 'Back' }));
    await user.click(screen.getByRole('button', { name: 'Back' }));
    await user.selectOptions(screen.getByLabelText('Node'), 'Córdoba 1');
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.click(await screen.findByRole('button', { name: 'Next' }));
    const second = await screen.findByLabelText('SourceTV');
    await within(second).findByRole('option', { name: '10.0.0.2:27020' });
    expect(second).toHaveValue('');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(body).toBeDefined());
    expect(body).toMatchObject({ nodeId: otherNode });
    expect((body as { ports: unknown }).ports).toEqual({});
  });
});

describe('a game server page', () => {
  it('shows install progress live and cancels an update', async () => {
    const cancel = vi.fn(() => json(details()));
    const updating = details({
      ...summary,
      activeOperation: {
        id: '0192f1a0-0000-7000-8000-0000000000b9',
        type: 'serverUpdate',
        status: 'running',
        stage: 'downloading',
        percent: 42.5,
        errorCode: null,
        errorMessage: null,
        createdAt: '2026-10-02T12:00:00Z',
        completedAt: null,
        cancellable: true,
        cancelRequested: false,
      },
    });
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${serverId}`]: () => json(updating),
      [`POST /api/v1/servers/${serverId}/operations/0192f1a0-0000-7000-8000-0000000000b9/cancel`]: cancel,
    });
    renderAt(`/servers/${serverId}`);
    const user = userEvent.setup();

    const progress = await screen.findByRole('progressbar', { name: 'Update' });
    expect(progress).toHaveAttribute('aria-valuenow', '43');
    expect(screen.getByText(/downloading/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(cancel).toHaveBeenCalledOnce();
  });

  it('updates the game, optionally verifying its files', async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`POST /api/v1/servers/${serverId}/update`]: async (request) => {
        body = await request.json();
        return json(details());
      },
    });
    renderAt(`/servers/${serverId}`);
    const user = userEvent.setup();

    expect(await screen.findByText('0.0.0.0:27015')).toBeInTheDocument();
    await user.click(screen.getByLabelText(/Also verify every installed file/));
    await user.click(screen.getByRole('button', { name: 'Update game' }));

    await vi.waitFor(() => expect(body).toEqual({ validate: true }));
  });

  it('saves only the settings that changed, never sending a secret it does not have', async () => {
    let body: unknown;
    const settings: Schemas['ServerSettings'] = {
      variables: [
        { definition: hostname, value: 'My server', hasValue: true },
        { definition: maxPlayers, value: null, hasValue: false },
        { definition: mode, value: null, hasValue: false },
        { definition: password, value: null, hasValue: true },
      ],
      extraArguments: ['-tickrate', '66'],
      allowExtraArguments: true,
      autoStart: false,
      restartOnCrash: true,
    };
    let stored = settings;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/settings`]: () => json(stored),
      // The stored settings come back changed, as from the real API (the form starts again from them).
      [`PUT /api/v1/servers/${serverId}/settings`]: async (request) => {
        body = await request.json();
        stored = {
          ...settings,
          variables: [
            { definition: hostname, value: 'Renamed', hasValue: true },
            { definition: maxPlayers, value: null, hasValue: false },
            { definition: mode, value: null, hasValue: false },
            { definition: password, value: null, hasValue: false },
          ],
          autoStart: true,
        };
        return json(stored);
      },
    });
    renderAt(`/servers/${serverId}?tab=settings`);
    const user = userEvent.setup();

    expect(await screen.findByLabelText('Server name')).toHaveValue('My server');
    expect(screen.getByText(/A value is set/)).toBeInTheDocument();
    await user.clear(screen.getByLabelText('Server name'));
    await user.type(screen.getByLabelText('Server name'), 'Renamed');
    await user.click(screen.getByLabelText('Clear the value of Join password'));
    await user.click(screen.getByLabelText("Start when the node's agent starts"));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Settings saved. They apply at the next start.')).toBeInTheDocument();
    expect(screen.getByLabelText('Server name')).toHaveValue('Renamed');
    expect(screen.queryByText(/A value is set/)).not.toBeInTheDocument();
    expect(body).toEqual({
      variables: { hostname: 'Renamed', password: '' },
      extraArguments: ['-tickrate', '66'],
      autoStart: true,
      restartOnCrash: true,
    });
  });

  it('sets CPU, memory and database limits, which others only see', async () => {
    let body: unknown;
    let limits: Schemas['ServerLimitsView'] = { cpuPercent: null, memoryMb: null, databases: 2 };
    const settings: Schemas['ServerSettings'] = {
      variables: [],
      extraArguments: [],
      allowExtraArguments: false,
      autoStart: false,
      restartOnCrash: true,
    };
    fakeApi({
      'GET /api/v1/auth/session': session({
        ...operator,
        permissions: [...operator.permissions, 'server.limits.edit'],
      }),
      [`GET /api/v1/servers/${serverId}`]: () => json({ ...details(), limits }),
      [`GET /api/v1/servers/${serverId}/settings`]: () => json(settings),
      [`PUT /api/v1/servers/${serverId}/limits`]: async (request) => {
        body = await request.json();
        limits = { cpuPercent: 25, memoryMb: 2048, databases: 4 };
        return json({ ...details(), limits });
      },
    });
    renderAt(`/servers/${serverId}?tab=settings`);
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('CPU limit (% of the node)'), '25');
    await user.type(screen.getByLabelText('Memory limit (MiB)'), '2048');
    await user.clear(screen.getByLabelText('Databases'));
    await user.type(screen.getByLabelText('Databases'), '4');
    await user.click(within(screen.getByRole('region', { name: 'Resources' })).getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Limits saved.')).toBeInTheDocument();
    expect(body).toEqual({ cpuPercent: 25, memoryMb: 2048, databases: 4 });
  });

  it('keeps the database limit when its field is empty and checks it before saving', async () => {
    const bodies: unknown[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session({
        ...operator,
        permissions: [...operator.permissions, 'server.limits.edit'],
      }),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/settings`]: () =>
        json({ variables: [], extraArguments: [], allowExtraArguments: false, autoStart: false, restartOnCrash: true }),
      [`PUT /api/v1/servers/${serverId}/limits`]: async (request) => {
        bodies.push(await request.json());
        return json(details());
      },
    });
    renderAt(`/servers/${serverId}?tab=settings`);
    const user = userEvent.setup();
    const resources = await screen.findByRole('region', { name: 'Resources' });

    await user.clear(screen.getByLabelText('Databases'));
    await user.type(screen.getByLabelText('Databases'), '51');
    await user.click(within(resources).getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('From 0 to 50.')).toBeInTheDocument();
    expect(screen.getByLabelText('Databases')).toHaveAttribute('aria-invalid', 'true');
    expect(bodies).toEqual([]);

    await user.clear(screen.getByLabelText('Databases'));
    await user.click(within(resources).getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Limits saved.')).toBeInTheDocument();
    expect(bodies).toEqual([{ cpuPercent: null, memoryMb: null }]);
  });

  it('does not remove a limit whose field holds something that is not a number', async () => {
    const bodies: unknown[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session({
        ...operator,
        permissions: [...operator.permissions, 'server.limits.edit'],
      }),
      [`GET /api/v1/servers/${serverId}`]: () =>
        json({ ...details(), limits: { cpuPercent: 50, memoryMb: null, databases: 2 } }),
      [`GET /api/v1/servers/${serverId}/settings`]: () =>
        json({ variables: [], extraArguments: [], allowExtraArguments: false, autoStart: false, restartOnCrash: true }),
      [`PUT /api/v1/servers/${serverId}/limits`]: async (request) => {
        bodies.push(await request.json());
        return json(details());
      },
    });
    renderAt(`/servers/${serverId}?tab=settings`);
    const user = userEvent.setup();
    const resources = await screen.findByRole('region', { name: 'Resources' });
    const cpu = screen.getByLabelText('CPU limit (% of the node)');

    // A half-typed "1e": the browser reports the value as empty, and that it cannot read what is typed.
    Object.defineProperty(cpu, 'validity', { configurable: true, value: { badInput: true, valid: false } });
    await user.clear(cpu);
    await user.click(within(resources).getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Type a number, or leave it empty for no limit.')).toBeInTheDocument();
    expect(cpu).toHaveAttribute('aria-invalid', 'true');
    expect(bodies).toEqual([]);
  });

  it('does not put a variable back to its default when its field holds something that is not a number', async () => {
    const bodies: unknown[] = [];
    const settings: Schemas['ServerSettings'] = {
      variables: [{ definition: maxPlayers, value: '24', hasValue: true }],
      extraArguments: [],
      allowExtraArguments: false,
      autoStart: false,
      restartOnCrash: true,
    };
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/settings`]: () => json(settings),
      [`PUT /api/v1/servers/${serverId}/settings`]: async (request) => {
        bodies.push(await request.json());
        return json(settings);
      },
    });
    renderAt(`/servers/${serverId}?tab=settings`);
    const user = userEvent.setup();
    const slots = await screen.findByLabelText('Slots');

    // A half-typed "1e": the browser reports the value as empty, and that it cannot read what is typed.
    Object.defineProperty(slots, 'validity', { configurable: true, value: { badInput: true, valid: false } });
    await user.clear(slots);
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Type a whole number.')).toBeInTheDocument();
    expect(slots).toHaveAttribute('aria-invalid', 'true');
    expect(bodies).toEqual([]);
  });

  it('keeps the settings form when reloading them fails', async () => {
    let fail = false;
    const settings: Schemas['ServerSettings'] = {
      variables: [{ definition: hostname, value: 'My server', hasValue: true }],
      extraArguments: [],
      allowExtraArguments: false,
      autoStart: false,
      restartOnCrash: true,
    };
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/settings`]: () => (fail ? problem(500, 'unexpected') : json(settings)),
    });
    const router = createMemoryRouter(routes, { initialEntries: [`/servers/${serverId}?tab=settings`] });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
    render(<App router={router} queryClient={queryClient} />);
    expect(await screen.findByLabelText('Server name')).toHaveValue('My server');

    fail = true;
    await queryClient.refetchQueries({ queryKey: ['servers', serverId, 'settings'] });

    expect(
      await screen.findByText(/This page could not be refreshed and shows what was loaded last/),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Server name')).toHaveValue('My server');
  });

  it('moves a stopped server to a newer version of its template', async () => {
    let body: unknown;
    const newer = '0192f1a0-0000-7000-8000-0000000000f3';
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${serverId}`]: () =>
        json({ ...details(), templateUpgrade: { templateId: newer, version: '1.1.0' } }),
      [`PUT /api/v1/servers/${serverId}/template`]: async (request) => {
        body = await request.json();
        return json({ ...details(), templateVersion: '1.1.0', templateUpgrade: null });
      },
    });
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderAt(`/servers/${serverId}`);
    const user = userEvent.setup();

    expect(
      await screen.findByText('This server runs version 1.0.0 of its game template; version 1.1.0 is available.'),
    ).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Move to 1.1.0' }));

    await vi.waitFor(() => expect(body).toEqual({ templateId: newer }));
  });

  it('shows limits read-only to who cannot change them', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${serverId}`]: () =>
        json({ ...details(), limits: { cpuPercent: 50, memoryMb: 1024, databases: 2 } }),
      [`GET /api/v1/servers/${serverId}/settings`]: () =>
        json({ variables: [], extraArguments: [], allowExtraArguments: false, autoStart: false, restartOnCrash: true }),
    });
    renderAt(`/servers/${serverId}?tab=settings`);

    expect(await screen.findByText('CPU: up to 50 % of the node')).toBeInTheDocument();
    expect(screen.getByText(/Memory: up to 1(\.0)? GiB|Memory: up to 1(\.0)? GB/)).toBeInTheDocument();
    expect(screen.queryByLabelText('CPU limit (% of the node)')).not.toBeInTheDocument();
  });

  it('changes ports only while the server is stopped', async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/nodes/${nodeId}/allocations`]: () =>
        json([
          { ...allocations[0], serverId, serverName: 'Retakes', portName: 'game', inUse: true },
          allocations[1],
          allocations[2],
        ]),
      [`PUT /api/v1/servers/${serverId}/ports`]: async (request) => {
        body = await request.json();
        return json(details());
      },
    });
    renderAt(`/servers/${serverId}?tab=network`);
    const user = userEvent.setup();

    const tv = await screen.findByLabelText('SourceTV');
    await within(tv).findByRole('option', { name: '0.0.0.0:27020' });
    expect(within(tv).queryByRole('option', { name: '0.0.0.0:27025' })).not.toBeInTheDocument();
    await user.selectOptions(tv, '0.0.0.0:27020');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(body).toEqual({ ports: { game: allocationA, tv: allocationB } }));
  });
});

describe('the network tab', () => {
  it('does not ask for the node allocations of someone who may not see them', async () => {
    const asked = vi.fn(() => json(allocations));
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['server.view'] }),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/nodes/${nodeId}/allocations`]: asked,
    });
    renderAt(`/servers/${serverId}?tab=network`);

    expect(await screen.findByText('Game *')).toBeInTheDocument();
    // Without the list, the select still names the allocation the port has.
    expect(screen.getByLabelText('Game *')).toHaveDisplayValue('0.0.0.0:27015');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(asked).not.toHaveBeenCalled();
  });
});

describe('game templates', () => {
  it('previews a manifest before importing it', async () => {
    const imported = vi.fn(() => json(templateDetails, 201));
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/templates': () => json([template]),
      'POST /api/v1/templates/preview': () =>
        json({
          valid: true,
          errors: [],
          details: { ...templateDetails, template: { ...template, key: 'css-community', source: 'imported' } },
          alreadyImported: false,
          previousVersion: null,
          changedSections: [],
        }),
      'POST /api/v1/templates': imported,
    });
    renderAt('/templates');
    const user = userEvent.setup();

    expect(await screen.findByRole('table', { name: 'Games' })).toHaveTextContent('Counter-Strike: Source');
    await user.click(screen.getByRole('button', { name: 'Import a template' }));
    await user.click(screen.getByLabelText('Manifest (JSON)'));
    await user.paste('{"key": "css-community"}');
    await user.click(screen.getByRole('button', { name: 'Check' }));

    expect(await screen.findByText('srcds.exe')).toBeInTheDocument();
    expect(screen.getByText('232330')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Import' }));

    expect(await screen.findByText('Template imported.')).toBeInTheDocument();
    expect(imported).toHaveBeenCalledOnce();
  });

  it('shows why a manifest is refused', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/templates': () => json([template]),
      'POST /api/v1/templates/preview': () =>
        json({
          valid: false,
          errors: [{ path: 'install.steps[0]', message: 'Unknown step type run.' }],
          details: null,
          alreadyImported: false,
          previousVersion: null,
          changedSections: [],
        }),
    });
    renderAt('/templates');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Import a template' }));
    await user.click(screen.getByLabelText('Manifest (JSON)'));
    await user.paste('{}');
    await user.click(screen.getByRole('button', { name: 'Check' }));

    expect(await screen.findByText('install.steps[0]')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Import' })).not.toBeInTheDocument();
  });
});

describe('node allocations', () => {
  it('adds a range and explains overlaps', async () => {
    let attempt = 0;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/nodes/${nodeId}/allocations`]: () => json(allocations),
      [`POST /api/v1/nodes/${nodeId}/allocations`]: () =>
        ++attempt === 1
          ? problem(409, 'node.allocation_taken', 'taken')
          : json([{ ...allocations[0], id: '0192f1a0-0000-7000-8000-0000000000c3', port: 27030 }]),
    });
    const { AllocationsSection } = await import('../nodes/AllocationsSection');
    const { QueryClientProvider } = await import('@tanstack/react-query');
    const { MemoryRouter } = await import('react-router');
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <AllocationsSection nodeId={nodeId} canManage />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    const user = userEvent.setup();

    expect(await screen.findByText('0.0.0.0:27015')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Ports'), '27015-27030');
    await user.click(screen.getByRole('button', { name: 'Add' }));
    expect(await screen.findByText(/already has an allocation on that port/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Add' }));
    await vi.waitFor(() => expect(screen.getByLabelText('Ports')).toHaveValue(''));
  });

  it('asks before deleting an allocation', async () => {
    const deleted: string[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/nodes/${nodeId}/allocations`]: () => json(allocations),
      [`DELETE /api/v1/nodes/${nodeId}/allocations/${allocationA}`]: () => {
        deleted.push(allocationA);
        return new Response(null, { status: 204 });
      },
    });
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
    const { AllocationsSection } = await import('../nodes/AllocationsSection');
    const { QueryClientProvider } = await import('@tanstack/react-query');
    const { MemoryRouter } = await import('react-router');
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <AllocationsSection nodeId={nodeId} canManage />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    const user = userEvent.setup();
    const row = (await screen.findByText('0.0.0.0:27015')).closest('tr') as HTMLElement;
    // One held by a server the user cannot see is in use: it cannot be deleted.
    const hidden = screen.getByText('0.0.0.0:27025').closest('tr') as HTMLElement;
    expect(within(hidden).getByText('In use')).toBeInTheDocument();
    expect(within(hidden).queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();

    await user.click(within(row).getByRole('button', { name: 'Delete' }));
    expect(confirm).toHaveBeenLastCalledWith('Delete the allocation 0.0.0.0:27015?');
    expect(deleted).toEqual([]);

    await user.click(within(row).getByRole('button', { name: 'Delete' }));
    await vi.waitFor(() => expect(deleted).toEqual([allocationA]));
  });
});
