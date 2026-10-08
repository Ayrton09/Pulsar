import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { adminKeys } from '../admin/api';
import { invalidateServer } from '../servers/api';

export type AddonView = Schemas['AddonView'];

/** Under the server's keys: the live channel refreshes them when the server changes (an addon operation completing). */
export const addonKeys = {
  list: (serverId: string) => ['servers', serverId, 'addons'] as const,
};

export function useAddons(serverId: string) {
  return useQuery({
    queryKey: addonKeys.list(serverId),
    queryFn: () => unwrap(api.GET('/api/v1/servers/{id}/addons', { params: { path: { id: serverId } } })),
  });
}

/** Installing or removing one starts an operation on the server (and is audited). */
function useAddonsChanged(serverId: string) {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      invalidateServer(queryClient, serverId), // its lists too: they are under its key
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
}

/** Installs the addon, or brings it to its latest release when it is installed. */
export function useInstallAddon(serverId: string) {
  const changed = useAddonsChanged(serverId);
  return useMutation({
    mutationFn: (key: string) =>
      unwrap(api.POST('/api/v1/servers/{id}/addons/{key}', { params: { path: { id: serverId, key } } })),
    onSettled: changed,
  });
}

export function useRemoveAddon(serverId: string) {
  const changed = useAddonsChanged(serverId);
  return useMutation({
    mutationFn: (key: string) =>
      unwrap(api.DELETE('/api/v1/servers/{id}/addons/{key}', { params: { path: { id: serverId, key } } })),
    onSettled: changed,
  });
}
