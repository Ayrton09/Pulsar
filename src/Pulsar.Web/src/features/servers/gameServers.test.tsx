import { QueryClient } from '@tanstack/react-query';
import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import i18next from 'i18next';
import { initI18n, loadLanguage } from '../../app/i18n';
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
  templateKey: 'css',
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
      {
        name: 'game',
        label: 'Game',
        required: true,
        allocationId: allocationA,
        ipAddress: '0.0.0.0',
        port: 27015,
        default: 27015,
      },
      {
        name: 'tv',
        label: 'SourceTV',
        required: false,
        allocationId: null,
        ipAddress: null,
        port: null,
        default: 27020,
      },
    ],
    recentOperations: [],
    limits: { cpuPercent: null, memoryMb: null, databases: 2 },
    hasAddons: false,
    hasMods: false,
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
  it('asks for the ports alone when the game needs nothing else, checking them before sending', async () => {
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
    await user.selectOptions(screen.getByLabelText('Game'), 'Counter-Strike: Source');
    expect(await screen.findByText(/about 4 GB of disk and 1 GB of memory/)).toBeInTheDocument();
    // Its name, slots, mode and password have values of the template's own: they are changed once it exists.
    expect(screen.getAllByRole('listitem').map((step) => step.textContent)).toEqual(['1. Game and node', '2. Ports']);
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(screen.queryByLabelText('Slots')).not.toBeInTheDocument();
    expect(screen.getByText(/rest of the game's options are changed in its Settings tab/)).toBeInTheDocument();

    // Whoever manages the node types numbers: the game's own to start with, saying where each lands.
    const game = await screen.findByLabelText('Game *');
    expect(game).toHaveValue('27015');
    expect(screen.getByText(/Uses the node's allocation 0\.0\.0\.0:27015\./)).toBeInTheDocument();
    const tv = screen.getByLabelText('SourceTV');
    expect(tv).toHaveValue('');
    expect(screen.getByText(/Off\. Type a port to turn it on\./)).toBeInTheDocument();
    // Another server's port (one the user cannot see) is refused before anything is sent.
    await user.type(tv, '27025');
    expect(screen.getByText('Another server uses port 27025.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Create' }));
    expect(body).toBeUndefined();
    await user.clear(tv);
    await user.type(tv, '27021');
    expect(screen.getByText(/New: Pulsar adds 0\.0\.0\.0:27021 to the node's allocations\./)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByRole('heading', { name: 'Retakes' })).toBeInTheDocument();
    // The template's own values, and every port by number.
    expect(body).toEqual({
      name: 'Retakes',
      description: null,
      nodeId,
      templateId,
      variables: {},
      portNumbers: { game: 27015, tv: 27021 },
    });
  });

  it("offers the node's free allocations to whoever cannot add ports to it, the game's own first", async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['server.view', 'server.create'] }),
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
    await user.selectOptions(screen.getByLabelText('Game'), 'Counter-Strike: Source');
    await user.click(await screen.findByRole('button', { name: 'Next' }));

    const game = await screen.findByLabelText('Game *');
    expect(screen.getByText(/Only whoever manages the node can add ports to it/)).toBeInTheDocument();
    expect(game).toHaveDisplayValue('0.0.0.0:27015');
    expect(within(game).queryByRole('option', { name: '0.0.0.0:27025' })).not.toBeInTheDocument();
    expect(screen.getByLabelText('SourceTV')).toHaveValue('');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(body).toBeDefined());
    expect(body).toMatchObject({ ports: { game: allocationA } });
    expect(body).not.toHaveProperty('portNumbers');
  });
});

describe('the list of games', () => {
  it('shows each game once on the Games page, at its newest version', async () => {
    const newer = { ...template, id: '0192f1a0-0000-7000-8000-0000000000f3', version: '1.1.0' };
    const other = { ...template, id: '0192f1a0-0000-7000-8000-0000000000f4', key: 'valheim', name: 'Valheim' };
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/templates': () => json([template, newer, other]),
      'GET /api/v1/appearance': () => json({ steamArtwork: true }),
    });
    renderAt('/templates');

    const games = within(await screen.findByRole('list', { name: 'Games' }));
    // The older version stays for the servers made from it, not as a second card.
    expect(await games.findAllByRole('listitem')).toHaveLength(2);
    const source = within(games.getByText('Counter-Strike: Source').closest('li') ?? document.body);
    expect(source.getByText(/1\.1\.0/)).toBeInTheDocument();
    expect(games.getAllByText('Counter-Strike: Source')).toHaveLength(1);
  });

  it("describes a bundled game in the reader's language, and an imported one as its author did", async () => {
    const bundled = { ...template, key: 'valheim', name: 'Valheim', description: 'Valheim dedicated server.' };
    const imported = {
      ...template,
      id: '0192f1a0-0000-7000-8000-0000000000f6',
      key: 'rust',
      name: 'Rust (community)',
      version: '9.0.0',
      description: 'A community build of Rust.',
      source: 'imported' as const,
    };
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/templates': () => json([bundled, imported]),
      'GET /api/v1/appearance': () => json({ steamArtwork: false }),
    });
    const previous = i18next.language;
    await loadLanguage('es');
    await act(() => i18next.changeLanguage('es'));
    try {
      renderAt('/templates');

      const spanish = i18next.getResource('es', 'common', 'gameText.valheim.description') as string;
      expect(await screen.findByText(spanish)).toBeInTheDocument();
      expect(screen.queryByText('Valheim dedicated server.')).not.toBeInTheDocument();
      // Rust has a Spanish description too, but this version's is its author's.
      expect(screen.getByText('A community build of Rust.')).toBeInTheDocument();
    } finally {
      await act(() => i18next.changeLanguage(previous));
    }
  });

  it('offers the newest version of each game, by its name alone', async () => {
    const newer = { ...template, id: '0192f1a0-0000-7000-8000-0000000000f3', version: '1.1.0' };
    const other = { ...template, id: '0192f1a0-0000-7000-8000-0000000000f4', key: 'valheim', name: 'Valheim' };
    // Another game that took the same name (imported under a key of its own).
    const namesake = {
      ...template,
      id: '0192f1a0-0000-7000-8000-0000000000f5',
      key: 'css-community',
      version: '2.0.0',
      source: 'imported' as const,
    };
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/servers': () => json([]),
      'GET /api/v1/nodes': () => json([{ id: nodeId, name: 'Buenos Aires 1', connected: true, status: 'active' }]),
      'GET /api/v1/templates': () => json([template, newer, other, namesake]),
    });
    renderAt('/servers');
    await userEvent.click(await screen.findByRole('button', { name: 'Create server' }));

    const games = within(screen.getByLabelText('Game'));
    // A template's own version would read as the game's; an older one is for the servers made from it.
    expect(await games.findByRole('option', { name: 'Valheim' })).toBeInTheDocument();
    expect(games.getByRole('option', { name: 'Counter-Strike: Source (template 1.1.0)' })).toHaveValue(newer.id);
    expect(games.getByRole('option', { name: 'Counter-Strike: Source (template 2.0.0)' })).toHaveValue(namesake.id);
    expect(games.queryByRole('option', { name: /1\.0\.0/ })).not.toBeInTheDocument();
  });
});

describe('a required choice', () => {
  it('starts unchosen, so a terms of use with one option can be accepted (and must be)', async () => {
    let body: unknown;
    const eula = variable({
      key: 'eula',
      label: 'Game EULA',
      type: 'enum',
      required: true,
      options: [{ value: 'accepted', label: 'I accept it' }],
    });
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/servers': () => json([]),
      'GET /api/v1/nodes': () => json([{ id: nodeId, name: 'Buenos Aires 1', connected: true, status: 'active' }]),
      'GET /api/v1/templates': () => json([template]),
      [`GET /api/v1/templates/${templateId}`]: () => json({ ...templateDetails, variables: [eula] }),
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
    await user.selectOptions(screen.getByLabelText('Game'), 'Counter-Strike: Source');
    await user.click(screen.getByRole('button', { name: 'Next' }));

    // Not shown as accepted while it is not.
    const choice = await screen.findByLabelText('Game EULA *');
    expect(choice).toHaveValue('');
    expect(within(choice).getByRole('option', { name: 'Choose…' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(screen.getByText('Required.')).toBeInTheDocument();

    await user.selectOptions(choice, 'I accept it');
    expect(choice).toHaveValue('accepted');
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await screen.findByLabelText('Game *');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByRole('heading', { name: 'Retakes' })).toBeInTheDocument();
    expect(body).toMatchObject({ variables: { eula: 'accepted' } });
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
    await user.selectOptions(screen.getByLabelText('Game'), 'Counter-Strike: Source');
    await user.click(await screen.findByRole('button', { name: 'Next' }));
    const first = await screen.findByLabelText('SourceTV');
    await user.type(first, '27020');
    expect(screen.getByText(/Uses the node's allocation 0\.0\.0\.0:27020\./)).toBeInTheDocument();

    // Back to the first step and another node.
    await user.click(screen.getByRole('button', { name: 'Back' }));
    await user.selectOptions(screen.getByLabelText('Node'), 'Córdoba 1');
    await user.click(screen.getByRole('button', { name: 'Next' }));
    const second = await screen.findByLabelText('SourceTV');
    expect(second).toHaveValue('');
    // That node has no allocation on the game's port: it gets one.
    expect(screen.getByLabelText('Game *')).toHaveValue('27015');
    expect(screen.getByText(/New: Pulsar adds 0\.0\.0\.0:27015/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(body).toBeDefined());
    expect(body).toMatchObject({ nodeId: otherNode, portNumbers: { game: 27015, tv: null } });
  });
});

/** The folded section a game's options are in. */
function optionsSection(summary: HTMLElement): HTMLElement {
  const section = summary.closest('details');
  if (!section) {
    throw new Error('The options are not in a section of their own.');
  }
  return section;
}

describe("a game's options", () => {
  const friendlyFire = variable({
    key: 'friendlyFire',
    label: 'Friendly fire',
    type: 'enum',
    options: [
      { value: '1', label: 'On' },
      { value: '0', label: 'Off' },
    ],
  });
  const roundTime = variable({ key: 'roundTime', label: 'Round time (min)', type: 'number', min: 1, max: 9 });
  const gameMode = variable({
    key: 'gameMode',
    label: 'Game mode',
    type: 'enum',
    default: 'casual',
    options: [
      { value: 'casual', label: 'Casual' },
      { value: 'competitive', label: 'Competitive' },
    ],
  });

  it('are not asked for when creating a server', async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/servers': () => json([]),
      'GET /api/v1/nodes': () => json([{ id: nodeId, name: 'Buenos Aires 1', connected: true, status: 'active' }]),
      'GET /api/v1/templates': () => json([template]),
      [`GET /api/v1/templates/${templateId}`]: () =>
        json({ ...templateDetails, variables: [hostname, gameMode, friendlyFire, roundTime] }),
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
    await user.selectOptions(screen.getByLabelText('Game'), 'Counter-Strike: Source');
    await user.click(await screen.findByRole('button', { name: 'Next' }));

    // Straight to the ports: the server's Settings tab has them once it exists.
    await screen.findByLabelText('Game *');
    expect(screen.queryByText(/More game options/)).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Game mode')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await vi.waitFor(() => expect(body).toBeDefined());
    // Nothing set: Pulsar leaves the game's lines as the server's files have them.
    expect((body as { variables: unknown }).variables).toEqual({});
  });

  it('are open in the settings tab', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/settings`]: () =>
        json({
          variables: [
            { definition: friendlyFire, value: '0', hasValue: true },
            { definition: roundTime, value: null, hasValue: false },
          ],
          extraArguments: [],
          allowExtraArguments: false,
          autoStart: false,
          restartOnCrash: true,
        }),
    });
    renderAt(`/servers/${serverId}?tab=settings`);

    const section = optionsSection(await screen.findByText('More game options (2)'));
    expect(section).toHaveAttribute('open');
    expect(within(section).getByLabelText('Friendly fire')).toHaveDisplayValue('Off');
    expect(screen.getByText(/Pulsar does not change them in the game's files/)).toBeInTheDocument();
  });

  it("are named in the reader's language, the template's English being the fallback", async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/settings`]: () =>
        json({
          variables: [
            { definition: friendlyFire, value: '0', hasValue: true },
            // Not one of the game's: an imported template's own text.
            { definition: variable({ key: 'custom', label: 'Custom setting' }), value: null, hasValue: false },
          ],
          extraArguments: [],
          allowExtraArguments: false,
          autoStart: false,
          restartOnCrash: true,
        }),
    });
    await loadLanguage('es');
    await act(() => i18next.changeLanguage('es'));
    try {
      renderAt(`/servers/${serverId}?tab=settings`);

      const fire = await screen.findByLabelText('Fuego amigo');
      expect(fire).toHaveDisplayValue('Desactivado');
      expect(screen.getByText('Los compañeros de equipo pueden hacerse daño.')).toBeInTheDocument();
      expect(screen.getByLabelText('Custom setting')).toBeInTheDocument();
    } finally {
      await act(() => i18next.changeLanguage('en'));
    }
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
    // The template's version, said to be one: a bare "(1.0.0)" reads as the game's.
    expect(screen.getByText('Counter-Strike: Source (template 1.0.0)')).toBeInTheDocument();
    await user.click(screen.getByLabelText(/Also verify every installed file/));
    await user.click(screen.getByRole('button', { name: 'Update game' }));

    await vi.waitFor(() => expect(body).toEqual({ validate: true }));
  });

  it("never carries a settings form being filled to another server's page, even with the same settings", async () => {
    const otherId = '0192f1a0-0000-7000-8000-0000000000ea';
    const other = { ...summary, id: otherId, name: 'Deathmatch' };
    // Both stored alike: the form's own key (its settings) does not tell the two servers apart.
    const settings: Schemas['ServerSettings'] = {
      variables: [{ definition: hostname, value: 'My server', hasValue: true }],
      extraArguments: [],
      allowExtraArguments: false,
      autoStart: false,
      restartOnCrash: true,
    };
    const saved: string[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${otherId}`]: () => json(details(other)),
      [`GET /api/v1/servers/${serverId}/settings`]: () => json(settings),
      [`GET /api/v1/servers/${otherId}/settings`]: () => json(settings),
      [`PUT /api/v1/servers/${serverId}/settings`]: () => {
        saved.push(serverId);
        return json(settings);
      },
      [`PUT /api/v1/servers/${otherId}/settings`]: () => {
        saved.push(otherId);
        return json(settings);
      },
    });
    // The other server first: going back to it later finds its page in the cache, shown at once.
    const router = renderAt(`/servers/${otherId}?tab=settings`);
    const user = userEvent.setup();
    expect(await screen.findByLabelText('Server name')).toHaveValue('My server');
    await act(() => router.navigate(`/servers/${serverId}?tab=settings`));
    expect(await screen.findByRole('heading', { level: 1, name: 'Retakes' })).toBeInTheDocument();
    await user.clear(await screen.findByLabelText('Server name'));
    await user.type(screen.getByLabelText('Server name'), 'Typed on the first server');

    await act(() => router.navigate(-1));

    expect(await screen.findByRole('heading', { level: 1, name: 'Deathmatch' })).toBeInTheDocument();
    expect(await screen.findByLabelText('Server name')).toHaveValue('My server');
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await vi.waitFor(() => expect(saved).toEqual([otherId]));
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

    expect(await screen.findByText('Type a number.')).toBeInTheDocument();
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

    // Its own allocation is its own, not another server's.
    const game = await screen.findByLabelText('Game *');
    expect(game).toHaveValue('27015');
    expect(screen.getByText(/Uses the node's allocation 0\.0\.0\.0:27015\./)).toBeInTheDocument();
    const tv = screen.getByLabelText('SourceTV');
    await user.type(tv, '27025');
    expect(screen.getByText('Another server uses port 27025.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    await user.clear(tv);
    await user.type(tv, '27020');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(body).toEqual({ portNumbers: { game: 27015, tv: 27020 } }));
  });

  it("offers the node's free allocations to whoever cannot add ports to it", async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session({
        ...adminUser,
        permissions: ['server.view', 'server.settings.edit', 'node.view'],
      }),
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

    expect(await screen.findByRole('list', { name: 'Games' })).toHaveTextContent('Counter-Strike: Source');
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

  it("starts creating a server with the game chosen from its card, and shows the game's artwork", async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(operator),
      'GET /api/v1/templates': () => json([template]),
      'GET /api/v1/nodes': () => json([{ id: nodeId, name: 'Buenos Aires 1', connected: true, status: 'active' }]),
      [`GET /api/v1/templates/${templateId}`]: () => json(templateDetails),
    });
    renderAt('/templates');
    const user = userEvent.setup();

    const card = within(await screen.findByRole('list', { name: 'Games' })).getByRole('listitem');
    await vi.waitFor(() =>
      expect(card.querySelector('img')).toHaveAttribute(
        'src',
        'https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/240/header.jpg',
      ),
    );
    await user.click(within(card).getByRole('button', { name: 'Create server for Counter-Strike: Source 1.0.0' }));

    const dialog = await screen.findByRole('dialog', { name: 'Create server' });
    expect(within(dialog).getByLabelText(/^Game/)).toHaveValue(templateId);
  });

  it('offers server creation to whoever may create servers on one node only', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session({
        ...adminUser,
        permissions: ['template.view'],
        nodePermissions: [{ id: nodeId, permissions: ['server.create'] }],
      }),
      'GET /api/v1/templates': () => json([template]),
    });
    renderAt('/templates');

    const card = within(await screen.findByRole('list', { name: 'Games' })).getByRole('listitem');
    expect(
      within(card).getByRole('button', { name: 'Create server for Counter-Strike: Source 1.0.0' }),
    ).toBeInTheDocument();
  });

  it('lets an administrator turn the artwork off: the games are then a list without pictures', async () => {
    let sent: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['template.view', 'settings.manage'] }),
      'GET /api/v1/templates': () => json([template]),
      'PUT /api/v1/appearance': async (request) => {
        sent = await request.json();
        return json({ steamArtwork: false });
      },
    });
    renderAt('/templates');
    const user = userEvent.setup();

    const card = within(await screen.findByRole('list', { name: 'Games' })).getByRole('listitem');
    await vi.waitFor(() => expect(card.querySelector('img')).not.toBeNull());
    await user.click(await screen.findByLabelText('Show the game artwork (Steam and minecraft.net)'));

    expect(await screen.findByRole('status')).toHaveTextContent('Artwork turned off.');
    expect(sent).toEqual({ steamArtwork: false });
    expect(screen.getByLabelText('Show the game artwork (Steam and minecraft.net)')).not.toBeChecked();
    // Not cards without their picture: a list, with neither pictures nor drawn tiles.
    const row = within(screen.getByRole('list', { name: 'Games' })).getByRole('listitem');
    expect(within(row).getByRole('heading', { name: 'Counter-Strike: Source' })).toBeInTheDocument();
    expect(row.querySelector('img')).toBeNull();
    expect(row.querySelector('svg')).toBeNull();
  });

  it('shows the artwork setting only to whoever manages the settings', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['template.view'] }),
      'GET /api/v1/templates': () => json([template]),
    });
    renderAt('/templates');

    expect(await screen.findByRole('list', { name: 'Games' })).toBeInTheDocument();
    expect(screen.queryByLabelText('Show the game artwork (Steam and minecraft.net)')).not.toBeInTheDocument();
  });

  it('offers no server creation to whoever may only view the games', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['template.view'] }),
      'GET /api/v1/templates': () => json([template]),
    });
    renderAt('/templates');

    const card = within(await screen.findByRole('list', { name: 'Games' })).getByRole('listitem');
    expect(card).toHaveTextContent('Counter-Strike: Source');
    expect(within(card).queryByRole('button')).not.toBeInTheDocument();
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
