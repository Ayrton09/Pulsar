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

const serverId = '0192f1a0-0000-7000-8000-0000000000e2';
const manager = { ...adminUser, permissions: ['server.view', 'server.install'] };
const viewer = { ...adminUser, permissions: ['server.view'] };

const server: Schemas['ServerSummary'] = {
  id: serverId,
  shortId: 'm1n3cr4f',
  name: 'Survival',
  nodeId: '0192f1a0-0000-7000-8000-0000000000a1',
  nodeName: 'Buenos Aires 1',
  nodeConnected: true,
  templateId: '0192f1a0-0000-7000-8000-0000000000f2',
  templateName: 'Minecraft: Java Edition',
  templateKey: 'minecraft',
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

function details(changes: Partial<Schemas['ServerSummary']> = {}, hasMods = true): Schemas['ServerDetails'] {
  return {
    server: { ...server, ...changes },
    description: null,
    autoStart: false,
    restartOnCrash: true,
    templateKey: 'minecraft',
    templateVersion: '1.4.0',
    canUpdate: true,
    ports: [],
    recentOperations: [],
    limits: { cpuPercent: null, memoryMb: null, databases: 2 },
    hasAddons: false,
    hasMods,
    gameVersion: '1.21.8',
  };
}

const fabricApi: Schemas['ServerModView'] = {
  id: '0192f1a0-0000-7000-8000-0000000000c1',
  projectId: 'P7dR8mSH',
  versionId: 'fapi0130',
  name: 'Fabric API',
  versionNumber: '0.130.0+1.21.8',
  path: 'mods/fabric-api-0.130.0+1.21.8.jar',
  size: 2_400_000,
  dependency: true,
  state: 'installed',
  installedAt: '2026-10-25T18:00:00Z',
  requiredBy: ['Lithium'],
};

const lithium: Schemas['ServerModView'] = {
  id: '0192f1a0-0000-7000-8000-0000000000c2',
  projectId: 'gvQqBUqZ',
  versionId: 'lith0181',
  name: 'Lithium',
  versionNumber: 'mc1.21.8-0.18.1',
  path: 'mods/lithium-fabric-0.18.1+mc1.21.8.jar',
  size: 780_000,
  dependency: false,
  state: 'installed',
  installedAt: '2026-10-25T18:00:00Z',
  requiredBy: [],
};

function mods(installed: Schemas['ServerModView'][], changes: Partial<Schemas['ModsView']> = {}): Schemas['ModsView'] {
  return {
    supported: true,
    catalog: 'modrinth',
    projectType: 'mod',
    folder: 'mods',
    loaders: ['fabric'],
    gameVersion: '1.21.8',
    unavailable: null,
    installed,
    ...changes,
  };
}

const sodium: Schemas['ModHitView'] = {
  projectId: 'AANobbMI',
  slug: 'sodium',
  title: 'Sodium',
  description: 'A modern rendering engine.',
  author: 'jellysquid3',
  downloads: 61_000_000,
  updated: '2026-10-20T00:00:00Z',
  installed: false,
};

const ferrite: Schemas['ModHitView'] = {
  projectId: 'uXXizFIs',
  slug: 'ferrite-core',
  title: 'FerriteCore',
  description: 'Memory usage optimizations.',
  author: 'malte0811',
  downloads: 40_000_000,
  updated: '2026-10-18T00:00:00Z',
  installed: false,
};

const lastFailure: Schemas['OperationView'] = {
  id: '0192f1a0-0000-7000-8000-0000000000d0',
  type: 'modsChange',
  status: 'failed',
  stage: null,
  percent: null,
  errorCode: 'mods.file_exists',
  errorMessage:
    "'mods/lithium.jar' is already on the server, and the panel did not put it there: delete or rename it first.",
  createdAt: '2026-10-25T19:00:00Z',
  completedAt: '2026-10-25T19:00:05Z',
  cancellable: false,
  cancelRequested: false,
};

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
  render(<App router={router} queryClient={queryClient} />);
  return router;
}

function row(name: string): HTMLElement {
  return screen.getByText(name, { selector: 'span' }).closest('tr') as HTMLElement;
}

function hit(title: string): HTMLElement {
  return screen.getByRole('heading', { name: title }).closest('li') as HTMLElement;
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('mods from the catalog', () => {
  it('is a tab only for games with a mod catalog', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details({}, false)),
    });

    renderAt(`/servers/${serverId}?tab=addons`);

    expect(await screen.findByRole('tab', { name: 'Overview', selected: true })).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Mods' })).not.toBeInTheDocument();
  });

  it('lists what is on the server, and what needs what, without asking the catalog for viewers', async () => {
    const api = fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/mods`]: () => json(mods([fabricApi, lithium])),
    });

    renderAt(`/servers/${serverId}?tab=addons`);

    await screen.findByRole('heading', { name: 'On the server' });
    expect(row('Fabric API')).toHaveTextContent('Dependency');
    expect(row('Fabric API')).toHaveTextContent('Needed by Lithium.');
    expect(row('Lithium')).toHaveTextContent('mc1.21.8-0.18.1');
    expect(
      screen.getByText(/Mods from Modrinth for this server's type and game version \(1\.21\.8\)/),
    ).toBeInTheDocument();
    // Seeing them is not changing them, and not searching for more.
    expect(screen.queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument();
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument();
    expect(
      api.mock.calls.some(([input]) => String(input instanceof Request ? input.url : input).includes('/mods/search')),
    ).toBe(false);
  });

  it('searches the catalog as the user types, page by page, and installs a project', async () => {
    const searched: string[] = [];
    const installs: unknown[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/mods`]: () => json(mods([fabricApi, lithium])),
      [`GET /api/v1/servers/${serverId}/mods/search`]: (request) => {
        const url = new URL(request.url);
        const query = url.searchParams.get('query') ?? '';
        const offset = Number(url.searchParams.get('offset'));
        searched.push(`${query}@${offset}`);
        return json(
          query === ''
            ? { hits: offset === 0 ? [sodium] : [ferrite], total: 2 }
            : { hits: [{ ...ferrite, title: 'FerriteCore' }], total: 1 },
        );
      },
      [`POST /api/v1/servers/${serverId}/mods`]: async (request) => {
        installs.push(await request.json());
        return json(mods([fabricApi, lithium]));
      },
    });
    renderAt(`/servers/${serverId}?tab=addons`);

    // The most downloaded first, then the next page.
    await screen.findByRole('heading', { name: 'Sodium' });
    expect(hit('Sodium')).toHaveTextContent('by jellysquid3 · 61M downloads');
    expect(within(hit('Sodium')).getByRole('link', { name: 'Sodium' })).toHaveAttribute(
      'href',
      'https://modrinth.com/mod/sodium',
    );
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await screen.findByRole('heading', { name: 'FerriteCore' });
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();

    await userEvent.type(screen.getByRole('searchbox', { name: 'Search mods' }), 'ferrite');
    await waitFor(() => expect(searched).toContain('ferrite@0'));
    // Typing pauses before it asks: not once a letter.
    expect(searched.filter((s) => s !== '@0' && s !== '@1')).toEqual(['ferrite@0']);
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Sodium' })).not.toBeInTheDocument());

    await userEvent.click(within(hit('FerriteCore')).getByRole('button', { name: 'Install' }));
    await waitFor(() => expect(installs).toEqual([{ projectId: 'uXXizFIs', versionId: null }]));
  });

  it('marks what the server has from its own list, and installs a chosen version', async () => {
    const installs: unknown[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/mods`]: () => json(mods([fabricApi, lithium])),
      [`GET /api/v1/servers/${serverId}/mods/search`]: () =>
        json({ hits: [{ ...sodium, projectId: lithium.projectId, title: 'Lithium', slug: 'lithium' }], total: 1 }),
      [`GET /api/v1/servers/${serverId}/mods/projects/${lithium.projectId}/versions`]: () =>
        json({
          versions: [
            {
              id: 'lith0190',
              name: 'Lithium 0.19.0 beta',
              versionNumber: 'mc1.21.8-0.19.0',
              versionType: 'beta',
              published: '2026-10-24T00:00:00Z',
            },
            {
              id: 'lith0181',
              name: 'mc1.21.8-0.18.1',
              versionNumber: 'mc1.21.8-0.18.1',
              versionType: 'release',
              published: '2026-10-01T00:00:00Z',
            },
          ],
        }),
      [`POST /api/v1/servers/${serverId}/mods`]: async (request) => {
        installs.push(await request.json());
        return json(mods([fabricApi, lithium]));
      },
    });
    renderAt(`/servers/${serverId}?tab=addons`);
    await screen.findByRole('heading', { name: 'Lithium' });

    // The search's flag is not what counts: the server's list says it is on it.
    expect(hit('Lithium')).toHaveTextContent('Installed');
    expect(within(hit('Lithium')).queryByRole('button', { name: 'Install' })).not.toBeInTheDocument();

    await userEvent.click(within(hit('Lithium')).getByRole('button', { name: 'Versions' }));
    const dialog = await screen.findByRole('dialog', { name: 'Versions of Lithium' });
    expect(await within(dialog).findByText('mc1.21.8-0.19.0')).toBeInTheDocument();
    expect(within(dialog).getByText('Beta')).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Install this one' }));

    await waitFor(() => expect(installs).toEqual([{ projectId: 'gvQqBUqZ', versionId: 'lith0190' }]));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('checks for updates when asked, and updates one or all', async () => {
    const updates: unknown[] = [];
    let checks = 0;
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/mods`]: () => json(mods([fabricApi, lithium])),
      [`GET /api/v1/servers/${serverId}/mods/search`]: () => json({ hits: [], total: 0 }),
      [`GET /api/v1/servers/${serverId}/mods/updates`]: () => {
        checks++;
        return json({
          updates: [
            {
              modId: lithium.id,
              name: 'Lithium',
              versionNumber: lithium.versionNumber,
              newVersionId: 'lith0182',
              newVersionNumber: 'mc1.21.8-0.18.2',
            },
          ],
        });
      },
      [`POST /api/v1/servers/${serverId}/mods/update`]: async (request) => {
        updates.push(await request.json());
        return json(mods([fabricApi, lithium]));
      },
    });
    renderAt(`/servers/${serverId}?tab=addons`);
    await screen.findByRole('heading', { name: 'On the server' });
    expect(checks).toBe(0); // each check is a request to the catalog: only when asked

    await userEvent.click(screen.getByRole('button', { name: 'Check for updates' }));
    expect(await screen.findByText('1 update available.')).toBeInTheDocument();
    expect(row('Lithium')).toHaveTextContent('New: mc1.21.8-0.18.2');
    await userEvent.click(within(row('Lithium')).getByRole('button', { name: 'Update' }));
    await userEvent.click(screen.getByRole('button', { name: 'Update all' }));

    await waitFor(() => expect(updates).toEqual([{ modIds: [lithium.id] }, { modIds: null }]));
  });

  it('removes after asking, and names what still needs a mod in the language of the user', async () => {
    const removed: string[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/mods`]: () => json(mods([fabricApi, lithium])),
      [`GET /api/v1/servers/${serverId}/mods/search`]: () => json({ hits: [], total: 0 }),
      [`DELETE /api/v1/servers/${serverId}/mods/${fabricApi.id}`]: () =>
        problem(409, 'mods.required_by', 'Fabric API is needed by Lithium: remove Lithium first.'),
      [`DELETE /api/v1/servers/${serverId}/mods/${lithium.id}`]: () => {
        removed.push('Lithium');
        return json(mods([fabricApi]));
      },
    });
    renderAt(`/servers/${serverId}?tab=addons`);
    await screen.findByRole('heading', { name: 'On the server' });
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValue(true);

    await userEvent.click(within(row('Lithium')).getByRole('button', { name: 'Remove' }));
    expect(confirm).toHaveBeenCalledWith(
      'Remove Lithium from Survival? Its file is deleted; its configuration files and the mods it needed are kept.',
    );
    expect(removed).toEqual([]);
    await userEvent.click(within(row('Fabric API')).getByRole('button', { name: 'Remove' }));
    expect(await screen.findByText('Lithium needs Fabric API: remove it first.')).toBeInTheDocument();
    await userEvent.click(within(row('Lithium')).getByRole('button', { name: 'Remove' }));

    await waitFor(() => expect(removed).toEqual(['Lithium']));
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });

  it('waits for the server to stop, and for a change on its way to end', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details({ runtimeState: 'running', desiredState: 'running' })),
      [`GET /api/v1/servers/${serverId}/mods`]: () => json(mods([fabricApi, lithium])),
      [`GET /api/v1/servers/${serverId}/mods/search`]: () => json({ hits: [sodium], total: 1 }),
    });

    renderAt(`/servers/${serverId}?tab=addons`);

    expect(
      await screen.findByText('Stop the server to install, update or remove mods and plugins.'),
    ).toBeInTheDocument();
    expect(within(row('Lithium')).getByRole('button', { name: 'Remove' })).toBeDisabled();
    await screen.findByRole('heading', { name: 'Sodium' });
    expect(within(hit('Sodium')).getByRole('button', { name: 'Install' })).toBeDisabled();
  });

  it('says why a server cannot take any: its type, or a game version not known yet', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/mods`]: () =>
        json(mods([], { projectType: null, folder: null, loaders: [], unavailable: 'mods.type_unsupported' })),
    });

    renderAt(`/servers/${serverId}?tab=addons`);

    expect(
      await screen.findByText(
        'A server of this type takes no mods or plugins: choose another type in its settings, then update it.',
      ),
    ).toBeInTheDocument();
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument();
  });

  it('says why the last change failed on the node, until another one works', async () => {
    const operations = [lastFailure];
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json({ ...details(), recentOperations: operations }),
      [`GET /api/v1/servers/${serverId}/mods`]: () => json(mods([fabricApi])),
      [`GET /api/v1/servers/${serverId}/mods/search`]: () => json({ hits: [], total: 0 }),
    });

    renderAt(`/servers/${serverId}?tab=addons`);

    expect(
      await screen.findByText(
        "The last change failed: 'mods/lithium.jar' is already on the server, and the panel did not put it there: delete or rename it first.",
      ),
    ).toBeInTheDocument();
  });

  it('forgets a failed change once a later one worked', async () => {
    const failed = { ...lastFailure, id: '0192f1a0-0000-7000-8000-0000000000d1' };
    const worked = {
      ...lastFailure,
      id: '0192f1a0-0000-7000-8000-0000000000d2',
      status: 'succeeded' as const,
      errorCode: null,
      errorMessage: null,
    };
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      // Newest first, as the panel sends them.
      [`GET /api/v1/servers/${serverId}`]: () => json({ ...details(), recentOperations: [worked, failed] }),
      [`GET /api/v1/servers/${serverId}/mods`]: () => json(mods([fabricApi])),
      [`GET /api/v1/servers/${serverId}/mods/search`]: () => json({ hits: [], total: 0 }),
    });

    renderAt(`/servers/${serverId}?tab=addons`);

    await screen.findByRole('heading', { name: 'On the server' });
    expect(screen.queryByText(/The last change failed/)).not.toBeInTheDocument();
  });

  it('calls them plugins on a plugin server, and links to their page', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/mods`]: () =>
        json(mods([], { projectType: 'plugin', folder: 'plugins', loaders: ['paper', 'spigot', 'bukkit'] })),
      [`GET /api/v1/servers/${serverId}/mods/search`]: () =>
        json({
          hits: [{ ...sodium, projectId: 'Vebnzrzj', slug: 'luckperms', title: 'LuckPerms', author: 'Luck' }],
          total: 1,
        }),
    });

    renderAt(`/servers/${serverId}?tab=addons`);

    expect(await screen.findByText('No plugins from the catalog yet: search for them below.')).toBeInTheDocument();
    expect(screen.getByRole('searchbox', { name: 'Search plugins' })).toBeInTheDocument();
    await screen.findByRole('heading', { name: 'LuckPerms' });
    expect(within(hit('LuckPerms')).getByRole('link', { name: 'LuckPerms' })).toHaveAttribute(
      'href',
      'https://modrinth.com/plugin/luckperms',
    );
  });
});
