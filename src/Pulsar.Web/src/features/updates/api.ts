import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { invalidateAfterChange } from '../../lib/queries';
import { adminKeys } from '../admin/api';

export type UpdatesView = Schemas['UpdatesView'];
export type NodeUpdateView = Schemas['NodeUpdateView'];
export type UpdateRequestResult = Schemas['UpdateRequestResult'];

export const updateKeys = {
  all: ['updates'] as const,
};

/**
 * Pulsar's updates (Phase 19): the panel's version, the newest release when it is newer, and each node. While an update
 * is under way it is read again every few seconds: the node's agent reports each step.
 */
export function useUpdates(enabled = true) {
  return useQuery({
    queryKey: updateKeys.all,
    queryFn: () => unwrap(api.GET('/api/v1/updates')),
    enabled,
    staleTime: 60_000,
    refetchInterval: (query) => (query.state.data?.nodes.some((n) => n.last?.underWay) ? 3_000 : false),
  });
}

function useUpdatesChanged() {
  const queryClient = useQueryClient();
  return (view?: UpdatesView) => {
    if (view) {
      queryClient.setQueryData(updateKeys.all, view);
    }
    return invalidateAfterChange(queryClient, { queryKey: adminKeys.audit });
  };
}

export function useCheckForUpdates() {
  const changed = useUpdatesChanged();
  return useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/updates/check')),
    onSuccess: changed,
  });
}

export function useUpdateSettings() {
  const changed = useUpdatesChanged();
  return useMutation({
    mutationFn: (checkAutomatically: boolean) =>
      unwrap(api.PUT('/api/v1/updates/settings', { body: { checkAutomatically } })),
    onSuccess: changed,
  });
}

/** Asks the nodes to update: the password, and the code from the app when two-factor authentication is on. */
export function useApplyUpdate() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: Schemas['ApplyUpdateRequest']) => unwrap(api.POST('/api/v1/updates/apply', { body })),
    onSuccess: () => invalidateAfterChange(queryClient, { queryKey: updateKeys.all }, { queryKey: adminKeys.audit }),
  });
}
