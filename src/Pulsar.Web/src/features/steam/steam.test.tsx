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
const accountId = '0192f1a0-0000-7000-8000-0000000000b1';
const host = { ...adminUser, permissions: ['server.view', 'server.install', 'server.limits.edit'] };
const owner = { ...adminUser, permissions: ['server.view', 'server.install'] };

const operation = (changes: Partial<Schemas['OperationView']>): Schemas['OperationView'] => ({
  id: '0192f1a0-0000-7000-8000-0000000000c1',
  type: 'serverInstall',
  status: 'failed',
  stage: null,
  percent: null,
  errorCode: 'steamcmd.guard_required',
  errorMessage: 'Steam asked for a Steam Guard code.',
  createdAt: '2026-10-09T12:00:00Z',
  completedAt: '2026-10-09T12:00:09Z',
  cancellable: false,
  cancelRequested: false,
  ...changes,
});

function details(
  steam: Schemas['ServerSteamView'] | null,
  operations: Schemas['OperationView'][] = [],
): Schemas['ServerDetails'] {
  return {
    server: {
      id: serverId,
      shortId: 'k3x7m2qa',
      name: 'DayZ',
      nodeId: '0192f1a0-0000-7000-8000-0000000000a1',
      nodeName: 'Buenos Aires 1',
      nodeConnected: true,
      templateId: '0192f1a0-0000-7000-8000-0000000000f1',
      templateName: 'DayZ',
      templateKey: 'dayz',
      gameAddress: null,
      installState: 'failed',
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
    templateKey: 'dayz',
    templateVersion: '1.0.0',
    canUpdate: true,
    ports: [],
    recentOperations: operations,
    limits: { cpuPercent: null, memoryMb: null, databases: 2 },
    hasAddons: false,
    steam,
  };
}

const settings: Schemas['ServerSettings'] = {
  variables: [],
  extraArguments: [],
  allowExtraArguments: false,
  autoStart: false,
  restartOnCrash: true,
};

const account: Schemas['SteamAccountView'] = {
  id: accountId,
  name: 'Servers account',
  userName: 'pulsar_servers',
  servers: 0,
  createdAt: '2026-10-09T12:00:00Z',
  updatedAt: '2026-10-09T12:00:00Z',
};

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
  render(<App router={router} queryClient={queryClient} />);
  return queryClient;
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('steam accounts', () => {
  it('lets whoever hosts the server choose its account, and shows it to the others', async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(host),
      [`GET /api/v1/servers/${serverId}`]: () => json(details({ required: true, accountId: null, accountName: null })),
      [`GET /api/v1/servers/${serverId}/settings`]: () => json(settings),
      'GET /api/v1/steam-accounts': () => json({ accounts: [account] }),
      [`PUT /api/v1/servers/${serverId}/steam-account`]: async (request) => {
        body = await request.json();
        return json(details({ required: true, accountId, accountName: 'Servers account' }));
      },
    });
    renderAt(`/servers/${serverId}?tab=settings`);
    const user = userEvent.setup();

    const card = await screen.findByRole('region', { name: /Steam account/ });
    expect(card).toHaveTextContent('Account needed');
    await within(card).findByRole('option', { name: 'Servers account (pulsar_servers)' });
    await user.selectOptions(within(card).getByLabelText('Account'), 'Servers account (pulsar_servers)');
    await user.click(within(card).getByRole('button', { name: 'Save' }));

    expect(await screen.findByText(/Steam account saved/)).toBeInTheDocument();
    expect(body).toEqual({ steamAccountId: accountId });
  });

  it('keeps showing the account the server uses while the list loads, and says when it could not', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(host),
      [`GET /api/v1/servers/${serverId}`]: () =>
        json(details({ required: true, accountId, accountName: 'Servers account' })),
      [`GET /api/v1/servers/${serverId}/settings`]: () => json(settings),
      'GET /api/v1/steam-accounts': () => problem(500, 'unexpected'),
    });
    renderAt(`/servers/${serverId}?tab=settings`);

    const card = await screen.findByRole('region', { name: /Steam account/ });
    expect(await within(card).findByRole('alert')).toBeInTheDocument();
    expect(within(card).getByLabelText('Account')).toHaveDisplayValue('Servers account');
  });

  it('shows the account read-only without the right to change it', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(owner),
      [`GET /api/v1/servers/${serverId}`]: () =>
        json(details({ required: false, accountId, accountName: 'Servers account' })),
      [`GET /api/v1/servers/${serverId}/settings`]: () => json(settings),
    });
    renderAt(`/servers/${serverId}?tab=settings`);

    const card = await screen.findByRole('region', { name: /Steam account/ });
    expect(card).toHaveTextContent('Servers account');
    expect(within(card).queryByRole('combobox')).not.toBeInTheDocument();
  });

  it('asks for the Steam Guard code when Steam stopped the install, and runs it again with it', async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(owner),
      [`GET /api/v1/servers/${serverId}`]: () =>
        json(details({ required: true, accountId, accountName: 'Servers account' }, [operation({})])),
      [`POST /api/v1/servers/${serverId}/install/steam-guard`]: async (request) => {
        body = await request.json();
        return json(details({ required: true, accountId, accountName: 'Servers account' }));
      },
    });
    renderAt(`/servers/${serverId}`);
    const user = userEvent.setup();

    const prompt = await screen.findByRole('region', { name: 'Steam Guard' });
    const retry = within(prompt).getByRole('button', { name: 'Install again with the code' });
    await user.type(within(prompt).getByLabelText('Steam Guard code'), 'F7K');
    expect(retry).toBeDisabled();
    await user.type(within(prompt).getByLabelText('Steam Guard code'), '2Q');
    await user.click(retry);

    await vi.waitFor(() => expect(body).toEqual({ steamGuardCode: 'F7K2Q' }));
  });

  it('sends the code in capitals, as Steam writes it, even if it was typed in lower case', async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(owner),
      [`GET /api/v1/servers/${serverId}`]: () =>
        json(details({ required: true, accountId, accountName: 'Servers account' }, [operation({})])),
      [`POST /api/v1/servers/${serverId}/install/steam-guard`]: async (request) => {
        body = await request.json();
        return json(details({ required: true, accountId, accountName: 'Servers account' }));
      },
    });
    renderAt(`/servers/${serverId}`);
    const user = userEvent.setup();

    const prompt = await screen.findByRole('region', { name: 'Steam Guard' });
    await user.type(within(prompt).getByLabelText('Steam Guard code'), 'f7k2q');
    await user.click(within(prompt).getByRole('button', { name: 'Install again with the code' }));

    await vi.waitFor(() => expect(body).toEqual({ steamGuardCode: 'F7K2Q' }));
  });

  it('runs an update Steam stopped again with the code, verifying the files when asked', async () => {
    let body: unknown;
    fakeApi({
      'GET /api/v1/auth/session': session(owner),
      [`GET /api/v1/servers/${serverId}`]: () =>
        json(
          details({ required: true, accountId, accountName: 'Servers account' }, [operation({ type: 'serverUpdate' })]),
        ),
      [`POST /api/v1/servers/${serverId}/update`]: async (request) => {
        body = await request.json();
        return json(details({ required: true, accountId, accountName: 'Servers account' }));
      },
    });
    renderAt(`/servers/${serverId}`);
    const user = userEvent.setup();

    const prompt = await screen.findByRole('region', { name: 'Steam Guard' });
    await user.type(within(prompt).getByLabelText('Steam Guard code'), 'F7K2Q');
    await user.click(within(prompt).getByLabelText(/Also verify every installed file/));
    await user.click(within(prompt).getByRole('button', { name: 'Update again with the code' }));

    await vi.waitFor(() => expect(body).toEqual({ validate: true, steamGuardCode: 'F7K2Q' }));
  });

  it('does not ask for a code after another failure, or for an update it can ask with the update', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(owner),
      [`GET /api/v1/servers/${serverId}`]: () =>
        json(
          details(null, [operation({ errorCode: 'steamcmd.failed', errorMessage: 'SteamCMD exited with code 8.' })]),
        ),
    });
    renderAt(`/servers/${serverId}`);

    await screen.findByRole('tab', { name: 'Overview' });
    expect(screen.queryByRole('region', { name: 'Steam Guard' })).not.toBeInTheDocument();
  });

  it('adds an account on the settings page', async () => {
    const bodies: unknown[] = [];
    let accounts: Schemas['SteamAccountView'][] = [];
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['settings.manage'] }),
      'GET /api/v1/steam-accounts': () => json({ accounts }),
      'POST /api/v1/steam-accounts': async (request) => {
        bodies.push(await request.json());
        accounts = [account];
        return json(account, 201);
      },
    });
    const queryClient = renderAt('/admin/steam-accounts');
    // A server page seen before: it names its account, which may be this one.
    queryClient.setQueryData(['servers', 'some-server'], { stale: false });
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Add Steam account' }));
    const dialog = screen.getByRole('dialog', { name: 'Add Steam account' });
    await user.type(within(dialog).getByLabelText('Name'), 'Servers account');
    await user.type(within(dialog).getByLabelText('Steam account name'), 'pulsar_servers');
    await user.type(within(dialog).getByLabelText('Password'), 'a password');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('table', { name: 'Steam accounts' })).toHaveTextContent('pulsar_servers');
    expect(bodies).toEqual([{ name: 'Servers account', userName: 'pulsar_servers', password: 'a password' }]);
    expect(queryClient.getQueryState(['servers', 'some-server'])?.isInvalidated).toBe(true);
  });
});
