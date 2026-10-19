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

const serverId = '0192f1a0-0000-7000-8000-0000000000e1';
const manager = { ...adminUser, permissions: ['server.view', 'server.install'] };
const viewer = { ...adminUser, permissions: ['server.view'] };

const server: Schemas['ServerSummary'] = {
  id: serverId,
  shortId: 'k3x7m2qa',
  name: 'Retakes',
  nodeId: '0192f1a0-0000-7000-8000-0000000000a1',
  nodeName: 'Buenos Aires 1',
  nodeConnected: true,
  templateId: '0192f1a0-0000-7000-8000-0000000000f1',
  templateName: 'Counter-Strike 2',
  templateKey: 'cs2',
  gameAddress: null,
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

function details(changes: Partial<Schemas['ServerSummary']> = {}, hasAddons = true): Schemas['ServerDetails'] {
  return {
    server: { ...server, ...changes },
    description: null,
    autoStart: false,
    restartOnCrash: true,
    templateKey: 'cs2',
    templateVersion: '1.0.0',
    canUpdate: true,
    ports: [],
    recentOperations: [],
    limits: { cpuPercent: null, memoryMb: null, databases: 2 },
    hasAddons,
  };
}

const metamod: Schemas['AddonView'] = {
  key: 'metamod',
  name: 'Metamod:Source',
  description: 'Plugin loader the other server plugins run on.',
  license: 'zlib',
  url: 'https://www.metamodsource.net/',
  requires: [],
  conflicts: [],
  reinstallAfterUpdate: false,
  installedRelease: '2.0.0.1473',
  installedAt: '2026-10-05T18:00:00Z',
};

const cssharp: Schemas['AddonView'] = {
  key: 'counterstrikesharp',
  name: 'CounterStrikeSharp',
  description: 'Server plugins written in C#.',
  license: 'GPL-3.0',
  url: 'https://docs.cssharp.dev/',
  requires: ['metamod'],
  conflicts: [],
  reinstallAfterUpdate: false,
  installedRelease: null,
  installedAt: null,
};

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
  render(<App router={router} queryClient={queryClient} />);
  return router;
}

function card(name: string): HTMLElement {
  return screen.getByRole('heading', { name }).closest('section') as HTMLElement;
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('mods tab', () => {
  it('is there only for games with addons', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details({}, false)),
    });

    renderAt(`/servers/${serverId}?tab=addons`);

    expect(await screen.findByRole('tab', { name: 'Overview', selected: true })).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Mods' })).not.toBeInTheDocument();
  });

  it('lists the addons with what they need and what is installed', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/addons`]: () => json([metamod, cssharp]),
    });

    renderAt(`/servers/${serverId}?tab=addons`);

    await screen.findByRole('heading', { name: 'Metamod:Source' });
    expect(card('Metamod:Source')).toHaveTextContent('Installed · 2.0.0.1473');
    expect(card('Metamod:Source')).toHaveTextContent('Licence: zlib');
    expect(within(card('Metamod:Source')).getByRole('link', { name: 'Metamod:Source' })).toHaveAttribute(
      'href',
      'https://www.metamodsource.net/',
    );
    expect(within(card('Metamod:Source')).getByRole('link', { name: 'Metamod:Source' })).toHaveAccessibleDescription(
      '(opens in a new tab)',
    );
    expect(card('CounterStrikeSharp')).toHaveTextContent('Not installed');
    expect(card('CounterStrikeSharp')).toHaveTextContent('Needs Metamod:Source.');
    // Seeing them is not changing them.
    expect(screen.queryByRole('button', { name: 'Install' })).not.toBeInTheDocument();
  });

  it('installs, updates and removes after asking, showing why the panel refuses', async () => {
    const calls: string[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/addons`]: () => json([metamod, cssharp]),
      [`POST /api/v1/servers/${serverId}/addons/counterstrikesharp`]: () => {
        calls.push('install counterstrikesharp');
        return problem(409, 'addon.requires', 'CounterStrikeSharp needs Metamod:Source installed first.');
      },
      [`POST /api/v1/servers/${serverId}/addons/metamod`]: () => {
        calls.push('update metamod');
        return json([metamod, cssharp]);
      },
      [`DELETE /api/v1/servers/${serverId}/addons/metamod`]: () => {
        calls.push('remove metamod');
        return json([{ ...metamod, installedRelease: null, installedAt: null }, cssharp]);
      },
    });
    renderAt(`/servers/${serverId}?tab=addons`);
    await screen.findByRole('heading', { name: 'Metamod:Source' });
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValue(true);

    await userEvent.click(within(card('CounterStrikeSharp')).getByRole('button', { name: 'Install' }));
    expect(await screen.findByText('CounterStrikeSharp needs Metamod:Source installed first.')).toBeInTheDocument();
    await userEvent.click(within(card('Metamod:Source')).getByRole('button', { name: 'Update to latest' }));
    await userEvent.click(within(card('Metamod:Source')).getByRole('button', { name: 'Remove' }));
    expect(confirm).toHaveBeenCalledWith(
      'Remove Metamod:Source from Retakes? The files it installed are deleted, even if you changed them; files you added yourself are kept.',
    );
    await userEvent.click(within(card('Metamod:Source')).getByRole('button', { name: 'Remove' }));

    await waitFor(() => expect(calls).toEqual(['install counterstrikesharp', 'update metamod', 'remove metamod']));
  });

  it('names the addons a refusal is about, in the language of the user', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/addons`]: () =>
        json([{ ...metamod, installedRelease: null, installedAt: null }, cssharp]),
      [`POST /api/v1/servers/${serverId}/addons/counterstrikesharp`]: () =>
        problem(409, 'addon.requires', 'CounterStrikeSharp needs Metamod:Source installed first.'),
    });
    renderAt(`/servers/${serverId}?tab=addons`);
    await screen.findByRole('heading', { name: 'CounterStrikeSharp' });

    await userEvent.click(within(card('CounterStrikeSharp')).getByRole('button', { name: 'Install' }));

    expect(await screen.findByText('CounterStrikeSharp needs Metamod:Source: install it first.')).toBeInTheDocument();
  });

  it('shows the error of the last change only', async () => {
    let removals = 0;
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/addons`]: () => json([metamod, cssharp]),
      [`POST /api/v1/servers/${serverId}/addons/counterstrikesharp`]: () =>
        problem(409, 'addon.requires', 'CounterStrikeSharp needs Metamod:Source installed first.'),
      [`DELETE /api/v1/servers/${serverId}/addons/metamod`]: () =>
        ++removals === 1 ? problem(409, 'server.busy', 'The server is busy.') : json([metamod, cssharp]),
    });
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderAt(`/servers/${serverId}?tab=addons`);
    await screen.findByRole('heading', { name: 'Metamod:Source' });

    await userEvent.click(within(card('CounterStrikeSharp')).getByRole('button', { name: 'Install' }));
    expect(await screen.findByText('CounterStrikeSharp needs Metamod:Source installed first.')).toBeInTheDocument();

    // A failed removal says its own error, not the install's.
    await userEvent.click(within(card('Metamod:Source')).getByRole('button', { name: 'Remove' }));
    await waitFor(() =>
      expect(screen.queryByText('CounterStrikeSharp needs Metamod:Source installed first.')).not.toBeInTheDocument(),
    );
    expect(screen.getByRole('alert')).toBeInTheDocument();

    // A removal that works leaves no error at all.
    await userEvent.click(within(card('Metamod:Source')).getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
    expect(removals).toBe(2);
  });

  it('waits for the server to stop', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details({ runtimeState: 'running', desiredState: 'running' })),
      [`GET /api/v1/servers/${serverId}/addons`]: () => json([metamod, cssharp]),
    });

    renderAt(`/servers/${serverId}?tab=addons`);

    expect(await screen.findByText('Stop the server to install, update or remove mods.')).toBeInTheDocument();
    expect(within(card('CounterStrikeSharp')).getByRole('button', { name: 'Install' })).toBeDisabled();
    expect(within(card('Metamod:Source')).getByRole('button', { name: 'Remove' })).toBeDisabled();
  });
});
