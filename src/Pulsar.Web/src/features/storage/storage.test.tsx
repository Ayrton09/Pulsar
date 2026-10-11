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

const settingsManager = { ...adminUser, permissions: [...adminUser.permissions, 'settings.manage'] };

const r2: Schemas['BackupStorageView'] = {
  id: '0192f1a0-0000-7000-8000-0000000000e9',
  name: 'Cloudflare R2',
  endpoint: 'https://abc.r2.cloudflarestorage.com',
  region: 'auto',
  bucket: 'pulsar-backups',
  prefix: 'pulsar/',
  accessKeyId: 'AKIDEXAMPLE',
  pathStyle: true,
  servers: 2,
  copies: 14,
  createdAt: '2026-10-04T12:00:00Z',
  updatedAt: '2026-10-04T12:00:00Z',
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

describe('backup storage page', () => {
  it('lists storages and keeps the secret when it is left empty', async () => {
    let updated: Schemas['BackupStorageRequest'] | null = null;
    fakeApi({
      'GET /api/v1/auth/session': session(settingsManager),
      'GET /api/v1/backup-storages': () => json({ storages: [r2] }),
      [`PUT /api/v1/backup-storages/${r2.id}`]: async (request) => {
        updated = (await request.json()) as Schemas['BackupStorageRequest'];
        return json({ ...r2, name: updated.name });
      },
    });
    const user = userEvent.setup();
    const queryClient = renderAt('/admin/backup-storage');
    // A server's Backups tab seen before: it lists the storages by name.
    queryClient.setQueryData(['servers', 'some-server', 'backups'], { stale: false });

    const table = await screen.findByRole('table', { name: 'Backup storage' });
    expect(table).toHaveTextContent('https://abc.r2.cloudflarestorage.com');
    expect(table).toHaveTextContent('pulsar-backups/pulsar/');
    expect(table).toHaveTextContent('2 servers · 14 copies');

    await user.click(within(table).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit storage' });
    expect(within(dialog).getByText('Leave it empty to keep the saved key.')).toBeInTheDocument();
    await user.clear(within(dialog).getByLabelText('Name'));
    await user.type(within(dialog).getByLabelText('Name'), 'R2 (offsite)');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(updated).toMatchObject({
      name: 'R2 (offsite)',
      secretAccessKey: null,
      bucket: 'pulsar-backups',
      pathStyle: true,
    });
    expect(queryClient.getQueryState(['servers', 'some-server', 'backups'])?.isInvalidated).toBe(true);
  });

  it('says why a storage in use cannot be deleted', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(settingsManager),
      'GET /api/v1/backup-storages': () => json({ storages: [r2] }),
      [`DELETE /api/v1/backup-storages/${r2.id}`]: () => problem(409, 'backup_storage.in_use'),
    });
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const user = userEvent.setup();
    renderAt('/admin/backup-storage');

    await user.click(await screen.findByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit storage' });
    await user.click(within(dialog).getByRole('button', { name: 'Delete' }));

    expect(await within(dialog).findByText(/Servers or backups use this storage/)).toBeInTheDocument();
  });
});
