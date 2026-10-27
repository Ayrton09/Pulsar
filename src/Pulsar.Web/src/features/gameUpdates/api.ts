import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { adminKeys } from '../admin/api';
import { invalidateServer } from '../servers/api';

export type GameUpdateView = Schemas['GameUpdateView'];
export type GameUpdatePolicy = Schemas['GameUpdatePolicy'];
export type GameUpdateSettings = Schemas['GameUpdateSettingsRequest'];
export type GameBranch = Schemas['GameBranchView'];

export const gameUpdatePolicies: GameUpdatePolicy[] = ['off', 'notify', 'install'];

/** Under the server's keys: the live channel refreshes them when the server changes (a check answered, an update moving on). */
export const gameUpdateKeys = {
  detail: (serverId: string) => ['servers', serverId, 'gameUpdates'] as const,
};

export function useGameUpdates(serverId: string) {
  return useQuery({
    queryKey: gameUpdateKeys.detail(serverId),
    queryFn: () => unwrap(api.GET('/api/v1/servers/{id}/game-updates', { params: { path: { id: serverId } } })),
  });
}

/** Each of these is audited, and some start an update on the server. */
function useChanged(serverId: string) {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      invalidateServer(queryClient, serverId),
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
}

export function useChangeGameUpdates(serverId: string) {
  const changed = useChanged(serverId);
  return useMutation({
    mutationFn: (body: GameUpdateSettings) =>
      unwrap(api.PUT('/api/v1/servers/{id}/game-updates', { params: { path: { id: serverId } }, body })),
    onSettled: changed,
  });
}

/** Asks the node now; the answer comes a little later (the live channel brings it). */
export function useCheckGameUpdate(serverId: string) {
  const changed = useChanged(serverId);
  return useMutation({
    mutationFn: () =>
      unwrap(api.POST('/api/v1/servers/{id}/game-updates/check', { params: { path: { id: serverId } } })),
    onSettled: changed,
  });
}

/** Warns the players, stops the server, updates it and starts it again if it was running. */
export function useInstallGameUpdate(serverId: string) {
  const changed = useChanged(serverId);
  return useMutation({
    mutationFn: () =>
      unwrap(api.POST('/api/v1/servers/{id}/game-updates/install', { params: { path: { id: serverId } } })),
    onSettled: changed,
  });
}

/** Stops the automatic update where it is, or keeps a pending one from starting. */
export function useCancelGameUpdate(serverId: string) {
  const changed = useChanged(serverId);
  return useMutation({
    mutationFn: () =>
      unwrap(api.POST('/api/v1/servers/{id}/game-updates/cancel', { params: { path: { id: serverId } } })),
    onSettled: changed,
  });
}

/** Backs the stopped server up, then installs the chosen Steam branch (1.0.14). */
export function useChangeSteamBranch(serverId: string) {
  const changed = useChanged(serverId);
  return useMutation({
    mutationFn: (branch: string) =>
      unwrap(
        api.PUT('/api/v1/servers/{id}/game-updates/branch', { params: { path: { id: serverId } }, body: { branch } }),
      ),
    onSettled: changed,
  });
}
