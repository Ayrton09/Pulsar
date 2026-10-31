import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, ApiError, unwrap, type Schemas } from '../../api/client';
import { invalidateAfterChange } from '../../lib/queries';
import { adminKeys } from '../admin/api';
import { sessionKey } from '../auth/api';
import { serverKeys } from '../servers/api';

export type QuotaView = Schemas['QuotaView'];
export type QuotaGame = Schemas['QuotaGameView'];
export type UserQuota = Schemas['UserQuotaResponse'];

export const quotaKeys = {
  mine: ['quota', 'mine'] as const,
  user: (id: string) => ['quota', 'user', id] as const,
};

/** A quota answered as missing is none: null, not an error. */
async function orNone<T>(request: Promise<T>): Promise<T | null> {
  try {
    return await request;
  } catch (error) {
    if (error instanceof ApiError && error.code === 'quota.not_found') {
      return null;
    }
    throw error;
  }
}

/** The signed-in user's quota (1.0.24), or null when they have none. */
export function useMyQuota(enabled = true) {
  return useQuery({
    queryKey: quotaKeys.mine,
    queryFn: () => orNone(unwrap(api.GET('/api/v1/account/quota'))),
    enabled,
  });
}

/** Creates a server within the user's quota: theirs from then on (the session's permissions change with it). */
export function useCreateOwnedServer() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: Schemas['CreateOwnedServerRequest']) => unwrap(api.POST('/api/v1/account/servers', { body })),
    onSuccess: () =>
      invalidateAfterChange(
        queryClient,
        { queryKey: serverKeys.all },
        { queryKey: quotaKeys.mine },
        { queryKey: sessionKey },
        { queryKey: adminKeys.audit },
      ),
  });
}

/** A user's quota, for whoever manages users; null when they have none. */
export function useUserQuota(id: string) {
  return useQuery({
    queryKey: quotaKeys.user(id),
    queryFn: () => orNone(unwrap(api.GET('/api/v1/users/{id}/quota', { params: { path: { id } } }))),
  });
}

export function useSetUserQuota() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: Schemas['SetUserQuotaRequest'] }) =>
      unwrap(api.PUT('/api/v1/users/{id}/quota', { params: { path: { id } }, body })),
    onSuccess: (quota, { id }) => {
      queryClient.setQueryData(quotaKeys.user(id), quota);
      return invalidateAfterChange(queryClient, { queryKey: adminKeys.audit });
    },
  });
}

export function useRemoveUserQuota() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE('/api/v1/users/{id}/quota', { params: { path: { id } } })),
    onSuccess: (_, id) => {
      queryClient.setQueryData(quotaKeys.user(id), null);
      return invalidateAfterChange(queryClient, { queryKey: adminKeys.audit });
    },
  });
}
