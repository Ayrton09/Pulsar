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
import { serverActions, serverDisplayState } from '../servers/serverStatus';

const serverId = '0192f1a0-0000-7000-8000-0000000000e1';
const manager = {
  ...adminUser,
  permissions: [
    'server.view',
    'server.backups.view',
    'server.backups.create',
    'server.backups.restore',
    'server.backups.delete',
  ],
};
const viewer = { ...adminUser, permissions: ['server.view', 'server.backups.view'] };

const server: Schemas['ServerSummary'] = {
  id: serverId,
  shortId: 'k3x7m2qa',
  name: 'Retakes',
  nodeId: '0192f1a0-0000-7000-8000-0000000000a1',
  nodeName: 'Buenos Aires 1',
  nodeConnected: true,
  templateId: '0192f1a0-0000-7000-8000-0000000000f1',
  templateName: 'Counter-Strike 2',
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

function details(changes: Partial<Schemas['ServerSummary']> = {}): Schemas['ServerDetails'] {
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
    limits: { cpuPercent: null, memoryMb: null },
  };
}

function backup(changes: Partial<Schemas['BackupView']>): Schemas['BackupView'] {
  return {
    id: '0192f1a0-0000-7000-8000-0000000000b1',
    name: 'Before the update',
    status: 'completed',
    size: 3 * 1024 * 1024,
    fileCount: 42,
    skippedFiles: 0,
    sha256: 'ab'.repeat(32),
    locked: false,
    createdVia: 'user',
    createdAt: '2026-10-03T10:00:00Z',
    completedAt: '2026-10-03T10:00:05Z',
    durationSeconds: 4.6,
    errorMessage: null,
    operationId: '0192f1a0-0000-7000-8000-0000000000c1',
    remoteStatus: 'none',
    remoteError: null,
    ...changes,
  };
}

const list: Schemas['BackupList'] = {
  limit: null,
  storageId: null,
  storages: [],
  backups: [
    backup({}),
    backup({
      id: '0192f1a0-0000-7000-8000-0000000000b2',
      name: 'Nightly',
      createdVia: 'schedule',
      locked: true,
      skippedFiles: 2,
    }),
    backup({
      id: '0192f1a0-0000-7000-8000-0000000000b3',
      name: 'Broken',
      status: 'failed',
      size: null,
      fileCount: null,
      sha256: null,
      durationSeconds: null,
      errorMessage: 'Not enough disk space.',
    }),
  ],
};

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
  render(<App router={router} queryClient={queryClient} />);
  return router;
}

function row(name: string): HTMLElement {
  const table = screen.getByRole('table', { name: 'Backups' });
  return within(table)
    .getAllByRole('row')
    .find((r) => within(r).queryByText(name) !== null) as HTMLElement;
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('backups tab', () => {
  it('lists backups with what they hold and how they ended', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/backups`]: () => json(list),
    });

    renderAt(`/servers/${serverId}?tab=backups`);

    await screen.findByRole('table', { name: 'Backups' });
    expect(row('Before the update')).toHaveTextContent('42 files · 5 s');
    expect(row('Before the update')).toHaveTextContent('3 MB');
    expect(row('Before the update')).toHaveTextContent('Ready');
    expect(screen.getByRole('link', { name: 'Download Before the update' })).toHaveAttribute(
      'href',
      `/api/v1/servers/${serverId}/backups/0192f1a0-0000-7000-8000-0000000000b1/download`,
    );
    expect(row('Nightly')).toHaveTextContent('Locked');
    expect(row('Nightly')).toHaveTextContent('scheduled');
    expect(row('Nightly')).toHaveTextContent('2 files left out (in use)');
    expect(screen.getByRole('button', { name: 'Delete Nightly' })).toBeDisabled();
    expect(row('Broken')).toHaveTextContent('Failed');
    expect(row('Broken')).toHaveTextContent('Not enough disk space.');
    expect(screen.queryByRole('link', { name: 'Download Broken' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Restore Broken' })).not.toBeInTheDocument();
  });

  it('makes a backup, warning when the server runs', async () => {
    const created: unknown[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details({ runtimeState: 'running', desiredState: 'running' })),
      [`GET /api/v1/servers/${serverId}/backups`]: () =>
        json({ limit: null, storageId: null, storages: [], backups: [] }),
      [`POST /api/v1/servers/${serverId}/backups`]: async (request) => {
        created.push(await request.json());
        return json(backup({ status: 'creating' }), 201);
      },
    });

    renderAt(`/servers/${serverId}?tab=backups`);

    expect(await screen.findByText('No backups yet.')).toBeInTheDocument();
    expect(screen.getByText('Stop the server to restore a backup.')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Create backup' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create a backup' });
    expect(within(dialog).getByText(/The server is running/)).toBeInTheDocument();
    await userEvent.type(within(dialog).getByLabelText('Name'), '  Before the map change  ');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create backup' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(created).toEqual([{ name: 'Before the map change' }]);
  });

  it('restores, locks and deletes after asking', async () => {
    const calls: string[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/backups`]: () => json(list),
      [`POST /api/v1/servers/${serverId}/backups/0192f1a0-0000-7000-8000-0000000000b1/restore`]: () => {
        calls.push('restore');
        return new Response(null, { status: 204 });
      },
      [`PUT /api/v1/servers/${serverId}/backups/0192f1a0-0000-7000-8000-0000000000b1/lock`]: async (request) => {
        calls.push(`lock ${JSON.stringify(await request.json())}`);
        return json(backup({ locked: true }));
      },
      [`DELETE /api/v1/servers/${serverId}/backups/0192f1a0-0000-7000-8000-0000000000b3`]: () => {
        calls.push('delete');
        return new Response(null, { status: 204 });
      },
    });
    renderAt(`/servers/${serverId}?tab=backups`);
    await screen.findByRole('table', { name: 'Backups' });
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValue(true);

    await userEvent.click(screen.getByRole('button', { name: 'Restore Before the update' }));
    expect(calls).toEqual([]);
    expect(confirm).toHaveBeenCalledWith(
      "Restore Before the update into Retakes? The backup's files replace the server's current ones; files the backup does not have stay as they are.",
    );
    await userEvent.click(screen.getByRole('button', { name: 'Restore Before the update' }));
    await userEvent.click(screen.getByRole('button', { name: 'Lock Before the update' }));
    await userEvent.click(screen.getByRole('button', { name: 'Delete Broken' }));

    await waitFor(() => expect(calls).toEqual(['restore', 'lock {"locked":true}', 'delete']));
  });

  it('keeps a number of backups, asking before deleting old ones', async () => {
    const limits: unknown[] = [];
    let current = list;
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/backups`]: () => json(current),
      [`PUT /api/v1/servers/${serverId}/backups/settings`]: async (request) => {
        const body = (await request.json()) as { limit: number | null };
        limits.push(body);
        current = { ...list, limit: body.limit };
        return json(current);
      },
    });
    renderAt(`/servers/${serverId}?tab=backups`);
    const input = await screen.findByLabelText('Backups to keep');
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);

    await userEvent.type(input, '0');
    expect(screen.getByText('A whole number from 1 to 1000, or empty to keep them all.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    await userEvent.clear(input);
    await userEvent.type(input, '2');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Saved.');
    expect(confirm).toHaveBeenCalledWith('Keep only the 2 newest backups? Older unlocked ones are deleted now.');
    expect(limits).toEqual([{ limit: 2 }]);
  });

  it('copies new backups to the chosen storage and shows how each copy went', async () => {
    const storageId = '0192f1a0-0000-7000-8000-0000000000e9';
    const choices: unknown[] = [];
    let current: Schemas['BackupList'] = {
      limit: null,
      storageId: null,
      storages: [{ id: storageId, name: 'Cloudflare R2' }],
      backups: [
        backup({ remoteStatus: 'copied' }),
        backup({
          id: '0192f1a0-0000-7000-8000-0000000000b2',
          name: 'Nightly',
          remoteStatus: 'failed',
          remoteError: 'Remote storage refused the credentials or the bucket (AccessDenied).',
        }),
      ],
    };
    fakeApi({
      'GET /api/v1/auth/session': session(manager),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/backups`]: () => json(current),
      [`PUT /api/v1/servers/${serverId}/backups/storage`]: async (request) => {
        const body = (await request.json()) as { storageId: string | null };
        choices.push(body);
        current = { ...current, storageId: body.storageId };
        return json(current);
      },
    });
    renderAt(`/servers/${serverId}?tab=backups`);

    await screen.findByRole('table', { name: 'Backups' });
    expect(row('Before the update')).toHaveTextContent('Copied to remote storage');
    expect(row('Nightly')).toHaveTextContent(
      'Not copied to remote storage: Remote storage refused the credentials or the bucket (AccessDenied).',
    );
    await userEvent.selectOptions(screen.getByLabelText('Copy new backups to'), storageId);
    const form = screen.getByLabelText('Copy new backups to').closest('form') as HTMLElement;
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(choices).toEqual([{ storageId }]));
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
  });

  it('shows only what a viewer may do', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      [`GET /api/v1/servers/${serverId}`]: () => json(details()),
      [`GET /api/v1/servers/${serverId}/backups`]: () => json(list),
    });

    renderAt(`/servers/${serverId}?tab=backups`);

    await screen.findByRole('table', { name: 'Backups' });
    expect(screen.getByRole('link', { name: 'Download Before the update' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Create backup' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Restore / })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Delete / })).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Backups to keep')).not.toBeInTheDocument();
  });
});

describe('servers during backups', () => {
  const operation = (type: Schemas['OperationType']): Schemas['OperationView'] => ({
    id: '0192f1a0-0000-7000-8000-0000000000c1',
    type,
    status: 'running',
    stage: 'archiving',
    percent: 40,
    errorCode: null,
    errorMessage: null,
    createdAt: '2026-10-03T10:00:00Z',
    completedAt: null,
    cancellable: type === 'backupCreate',
    cancelRequested: false,
  });

  it('can still start and restart during a backup, but not during a restore', () => {
    const backingUp = { ...server, activeOperation: operation('backupCreate') };
    const restoring = { ...server, activeOperation: operation('backupRestore') };

    expect(serverActions(backingUp).start).toBe(true);
    expect(serverActions({ ...backingUp, runtimeState: 'running' }).restart).toBe(true);
    expect(serverActions(backingUp).delete).toBe(false);
    expect(serverActions(restoring).start).toBe(false);
    expect(serverDisplayState(backingUp)).toBe('offline');
    expect(serverDisplayState(restoring)).toBe('restoring');
  });
});
