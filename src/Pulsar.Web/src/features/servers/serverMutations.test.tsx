import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeApi, json } from '../../test/fakeApi';
import { addonKeys, useInstallAddon } from '../addons/api';
import { adminKeys } from '../admin/api';
import { backupKeys, useCreateBackup } from '../backups/api';
import { scheduleKeys, useRunSchedule } from '../schedules/api';
import { serverKeys, useRenameServer, useServerCommand, useUpgradeTemplate } from './api';

const id = '0192f1a0-0000-7000-8000-0000000000s1';

function setUp() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  queryClient.setQueryData(serverKeys.detail(id), {});
  queryClient.setQueryData(serverKeys.settings(id), { values: {} });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  return { queryClient, wrapper };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('server mutations', () => {
  it('renaming refreshes the list, detail and audit without dropping settings being edited', async () => {
    fakeApi({ [`PUT /api/v1/servers/${id}/name`]: () => json({}) });
    const { queryClient, wrapper } = setUp();
    queryClient.setQueryData(serverKeys.all, []);
    queryClient.setQueryData(adminKeys.audit, {});
    const { result } = renderHook(() => useRenameServer(), { wrapper });

    await act(() => result.current.mutateAsync({ id, name: 'Renamed' }));

    expect(queryClient.getQueryState(serverKeys.all)?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(serverKeys.detail(id))?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(adminKeys.audit)?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(serverKeys.settings(id))?.isInvalidated).toBe(false);
  });
  it('leave the settings alone: an admin may be editing them', async () => {
    fakeApi({ [`POST /api/v1/servers/${id}/start`]: () => json({ status: 'accepted' }) });
    const { queryClient, wrapper } = setUp();
    const { result } = renderHook(() => useServerCommand(), { wrapper });

    await act(() => result.current.mutateAsync({ id, command: 'start' }));

    expect(queryClient.getQueryState(serverKeys.detail(id))?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(serverKeys.settings(id))?.isInvalidated).toBe(false);
  });

  it('of backups, addons and schedules refresh their own lists, not the settings', async () => {
    fakeApi({
      [`POST /api/v1/servers/${id}/backups`]: () => json({}),
      [`POST /api/v1/servers/${id}/addons/oxide`]: () => json({}),
      [`POST /api/v1/servers/${id}/schedules/t1/run`]: () => json({}),
    });
    const { queryClient, wrapper } = setUp();
    queryClient.setQueryData(backupKeys.list(id), {});
    queryClient.setQueryData(addonKeys.list(id), []);
    queryClient.setQueryData(scheduleKeys.runs(id, 't1'), {});
    const backup = renderHook(() => useCreateBackup(id), { wrapper }).result;
    const addon = renderHook(() => useInstallAddon(id), { wrapper }).result;
    const run = renderHook(() => useRunSchedule(id), { wrapper }).result;

    await act(() => backup.current.mutateAsync(null));
    await act(() => addon.current.mutateAsync('oxide'));
    await act(() => run.current.mutateAsync('t1'));

    expect(queryClient.getQueryState(backupKeys.list(id))?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(addonKeys.list(id))?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(scheduleKeys.runs(id, 't1'))?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(serverKeys.settings(id))?.isInvalidated).toBe(false);
  });

  it('refresh the settings when the game template changes', async () => {
    fakeApi({ [`PUT /api/v1/servers/${id}/template`]: () => json({}) });
    const { queryClient, wrapper } = setUp();
    const { result } = renderHook(() => useUpgradeTemplate(), { wrapper });

    await act(() => result.current.mutateAsync({ id, templateId: 't' }));

    expect(queryClient.getQueryState(serverKeys.settings(id))?.isInvalidated).toBe(true);
  });
});
