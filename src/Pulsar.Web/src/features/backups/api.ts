import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { adminKeys } from '../admin/api';
import { serverKeys } from '../servers/api';

export type BackupView = Schemas['BackupView'];
export type BackupList = Schemas['BackupList'];

/** Under the server's keys: the live channel refreshes them at every change of its backups (a backup completing, say). */
export const backupKeys = {
  list: (serverId: string) => ['servers', serverId, 'backups'] as const,
};

export function useBackups(serverId: string) {
  return useQuery({
    queryKey: backupKeys.list(serverId),
    queryFn: () => unwrap(api.GET('/api/v1/servers/{id}/backups', { params: { path: { id: serverId } } })),
  });
}

/** Backups change the server (its active operation) and the audit log. */
function useBackupsChanged(serverId: string) {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: serverKeys.detail(serverId) }),
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
}

export function useCreateBackup(serverId: string) {
  const changed = useBackupsChanged(serverId);
  return useMutation({
    mutationFn: (name: string | null) =>
      unwrap(api.POST('/api/v1/servers/{id}/backups', { params: { path: { id: serverId } }, body: { name } })),
    onSettled: changed,
  });
}

export function useRestoreBackup(serverId: string) {
  const changed = useBackupsChanged(serverId);
  return useMutation({
    mutationFn: (backupId: string) =>
      unwrap(
        api.POST('/api/v1/servers/{id}/backups/{backupId}/restore', { params: { path: { id: serverId, backupId } } }),
      ),
    onSettled: changed,
  });
}

export function useLockBackup(serverId: string) {
  const changed = useBackupsChanged(serverId);
  return useMutation({
    mutationFn: ({ backupId, locked }: { backupId: string; locked: boolean }) =>
      unwrap(
        api.PUT('/api/v1/servers/{id}/backups/{backupId}/lock', {
          params: { path: { id: serverId, backupId } },
          body: { locked },
        }),
      ),
    onSettled: changed,
  });
}

export function useDeleteBackup(serverId: string) {
  const changed = useBackupsChanged(serverId);
  return useMutation({
    mutationFn: (backupId: string) =>
      unwrap(api.DELETE('/api/v1/servers/{id}/backups/{backupId}', { params: { path: { id: serverId, backupId } } })),
    onSettled: changed,
  });
}

export function useBackupLimit(serverId: string) {
  const changed = useBackupsChanged(serverId);
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (limit: number | null) =>
      unwrap(api.PUT('/api/v1/servers/{id}/backups/settings', { params: { path: { id: serverId } }, body: { limit } })),
    onSuccess: (list) => {
      queryClient.setQueryData(backupKeys.list(serverId), list);
      return changed();
    },
  });
}

/** Where the browser downloads a backup from (a plain link: the panel streams it from the node). */
export function backupDownloadUrl(serverId: string, backupId: string): string {
  return `/api/v1/servers/${encodeURIComponent(serverId)}/backups/${encodeURIComponent(backupId)}/download`;
}
