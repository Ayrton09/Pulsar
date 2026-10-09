import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { adminKeys } from '../admin/api';

export type ServerDatabase = Schemas['ServerDatabaseView'];
export type ServerDatabaseCredentials = Schemas['ServerDatabaseCredentials'];
export type DatabaseHost = Schemas['DatabaseHostView'];
export type DatabaseHostRequest = Schemas['DatabaseHostRequest'];

export const databaseKeys = {
  server: (serverId: string) => ['servers', serverId, 'databases'] as const,
  hosts: ['database-hosts'] as const,
};

export function useServerDatabases(serverId: string) {
  return useQuery({
    queryKey: databaseKeys.server(serverId),
    queryFn: () => unwrap(api.GET('/api/v1/servers/{id}/databases', { params: { path: { id: serverId } } })),
  });
}

function useDatabasesChanged(serverId: string) {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: databaseKeys.server(serverId) }),
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
}

/** Creates one; the answer carries its password (shown once here, and to managers on request later). */
export function useCreateDatabase(serverId: string) {
  const changed = useDatabasesChanged(serverId);
  return useMutation({
    mutationFn: (body: Schemas['CreateDatabaseRequest']) =>
      unwrap(api.POST('/api/v1/servers/{id}/databases', { params: { path: { id: serverId } }, body })),
    onSuccess: changed,
  });
}

export function useDatabasePassword(serverId: string) {
  return useMutation({
    mutationFn: (databaseId: string) =>
      unwrap(
        api.GET('/api/v1/servers/{id}/databases/{databaseId}/password', {
          params: { path: { id: serverId, databaseId } },
        }),
      ),
  });
}

export function useRotateDatabasePassword(serverId: string) {
  const changed = useDatabasesChanged(serverId);
  return useMutation({
    mutationFn: (databaseId: string) =>
      unwrap(
        api.POST('/api/v1/servers/{id}/databases/{databaseId}/rotate-password', {
          params: { path: { id: serverId, databaseId } },
        }),
      ),
    onSuccess: changed,
  });
}

export function useDeleteDatabase(serverId: string) {
  const changed = useDatabasesChanged(serverId);
  return useMutation({
    mutationFn: (databaseId: string) =>
      unwrap(
        api.DELETE('/api/v1/servers/{id}/databases/{databaseId}', { params: { path: { id: serverId, databaseId } } }),
      ),
    onSuccess: changed,
  });
}

/**
 * Whether the panel leaves TLS optional for a new host at this address by default (DatabaseHost.IsLocalAddress):
 * localhost, loopback, private and link-local addresses. Host names other than localhost count as remote.
 */
export function isLocalAddress(address: string): boolean {
  const value = address.trim().toLowerCase();
  if (value === 'localhost' || value === '::1') {
    return true;
  }
  const v4 = /^(\d{1,3})\.(\d{1,3})\.\d{1,3}\.\d{1,3}$/.exec(value);
  if (v4) {
    const [a, b] = [Number(v4[1]), Number(v4[2])];
    return (
      a === 127 || a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168) || (a === 169 && b === 254)
    );
  }
  return /^f[cd][0-9a-f]{2}:/.test(value) || /^fe[89ab][0-9a-f]:/.test(value);
}

export function useDatabaseHosts() {
  return useQuery({ queryKey: databaseKeys.hosts, queryFn: () => unwrap(api.GET('/api/v1/database-hosts')) });
}

export function useDatabaseHostsChanged() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: databaseKeys.hosts }),
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
}
