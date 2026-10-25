import { QueryClient } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, noGameUpdates, session } from '../../test/fakeApi';

const serverId = '0192f1a0-0000-7000-8000-0000000000e7';
const updater = {
  ...adminUser,
  permissions: ['server.view', 'server.install', 'server.stop', 'server.start', 'server.console.write'],
};
const viewer = { ...adminUser, permissions: ['server.view'] };

const server: Schemas['ServerSummary'] = {
  id: serverId,
  shortId: 'u7x7m2qa',
  name: 'Survival',
  nodeId: '0192f1a0-0000-7000-8000-0000000000a1',
  nodeName: 'Buenos Aires 1',
  nodeConnected: true,
  templateId: '0192f1a0-0000-7000-8000-0000000000f1',
  templateName: 'Rust',
  templateKey: 'rust',
  gameAddress: null,
  gameUpdateAvailable: true,
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

const details: Schemas['ServerDetails'] = {
  server,
  description: null,
  autoStart: false,
  restartOnCrash: true,
  templateKey: 'rust',
  templateVersion: '1.3.0',
  canUpdate: true,
  ports: [],
  recentOperations: [],
  limits: { cpuPercent: null, memoryMb: null, databases: 2 },
  hasAddons: false,
};

const found: Schemas['GameUpdateView'] = {
  ...noGameUpdates,
  supported: true,
  canWaitForEmpty: true,
  canWarn: true,
  installed: '20476133',
  latest: '20500001',
  available: true,
  checkedAt: '2026-10-25T12:00:00Z',
};

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

describe('game updates', () => {
  it('shows an update and installs it the way an automatic one does', async () => {
    const installs: string[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(updater),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/game-updates`]: () => json(found),
      [`POST /api/v1/servers/${serverId}/game-updates/install`]: () => {
        installs.push('install');
        return json({
          ...found,
          running: {
            id: '0192f1a0-0000-7000-8000-0000000000c1',
            status: 'running',
            step: 0,
            steps: 3,
            stepKind: 'stop',
            wakeAt: null,
            createdAt: '2026-10-25T12:01:00Z',
            errorCode: null,
            message: null,
          },
        });
      },
    });
    vi.spyOn(window, 'confirm').mockReturnValue(true);

    renderAt(`/servers/${serverId}`);

    const card = await screen.findByRole('region', { name: 'Game updates' });
    expect(within(card).getByText('Update available')).toBeInTheDocument();
    expect(within(card).getByText('20476133')).toBeInTheDocument();
    expect(within(card).getByText('20500001')).toBeInTheDocument();
    await userEvent.click(within(card).getByRole('button', { name: 'Update and restart' }));
    await waitFor(() => expect(installs).toEqual(['install']));
  });

  it('says why an automatic update waits, and lets it be skipped', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(updater),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/game-updates`]: () =>
        json({ ...found, policy: 'install', whenEmpty: true, pending: true, playersOnline: 3 }),
    });

    renderAt(`/servers/${serverId}`);

    const card = await screen.findByRole('region', { name: 'Game updates' });
    expect(within(card).getByRole('status')).toHaveTextContent('The update waits until nobody plays (online now: 3).');
    expect(within(card).getByRole('button', { name: 'Skip this update' })).toBeInTheDocument();
  });

  it('is only shown for games whose updates can be checked, and changed only by those who may', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      [`GET /api/v1/servers/${serverId}`]: () =>
        json({ ...details, server: { ...server, gameUpdateAvailable: false } }),
    });

    renderAt(`/servers/${serverId}`);

    expect(await screen.findByRole('heading', { name: 'Survival' })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Game updates' })).not.toBeInTheDocument();
  });

  it('saves how the server follows its game updates', async () => {
    const saved: unknown[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(updater),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/settings`]: () =>
        json({ variables: [], extraArguments: [], allowExtraArguments: false, autoStart: false, restartOnCrash: true }),
      [`GET /api/v1/servers/${serverId}/game-updates`]: () => json(found),
      [`PUT /api/v1/servers/${serverId}/game-updates`]: async (request) => {
        saved.push(await request.json());
        return json({ ...found, policy: 'install', warningMinutes: 5 });
      },
    });

    renderAt(`/servers/${serverId}?tab=settings`);

    const card = await screen.findByRole('region', { name: 'Game updates' });
    await userEvent.selectOptions(within(card).getByLabelText('When the game has an update'), 'install');
    await userEvent.click(within(card).getByLabelText('Wait until nobody plays'));
    const minutes = within(card).getByLabelText('Warn the players (minutes before)');
    await userEvent.clear(minutes);
    await userEvent.type(minutes, '5');
    await userEvent.click(within(card).getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(saved).toEqual([
        {
          policy: 'install',
          whenEmpty: true,
          warningMinutes: 5,
          warningMessage: 'Restarting in {time} for a game update',
        },
      ]),
    );
  });
});
