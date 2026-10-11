import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { useServersChanged } from '../servers/api';

export type SteamAccount = Schemas['SteamAccountView'];
export type SteamAccountRequest = Schemas['SteamAccountRequest'];

export const steamKeys = { accounts: ['steam-accounts'] as const };

export function useSteamAccounts(enabled = true) {
  return useQuery({
    queryKey: steamKeys.accounts,
    queryFn: () => unwrap(api.GET('/api/v1/steam-accounts')),
    enabled,
  });
}

/** Accounts show up in their list and on the pages of the servers that sign in with them (and in the audit log). */
export function useSteamAccountsChanged() {
  const queryClient = useQueryClient();
  const serversChanged = useServersChanged();
  return () => Promise.all([queryClient.invalidateQueries({ queryKey: steamKeys.accounts }), serversChanged()]);
}

/** The account the server's SteamCMD signs in with from its next install or update (null: anonymous). */
export function useSetServerSteamAccount() {
  const changed = useServersChanged();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, steamAccountId }: { id: string; steamAccountId: string | null }) =>
      unwrap(api.PUT('/api/v1/servers/{id}/steam-account', { params: { path: { id } }, body: { steamAccountId } })),
    onSuccess: () => Promise.all([changed(), queryClient.invalidateQueries({ queryKey: steamKeys.accounts })]),
  });
}

/**
 * Runs the install or update Steam stopped again, with the Steam Guard code it asked for. The operation does not keep
 * whether an update verified every file: the prompt asks again (`validate`).
 */
export function useRetryWithSteamGuard() {
  const changed = useServersChanged();
  return useMutation({
    mutationFn: ({
      id,
      kind,
      code,
      validate,
    }: {
      id: string;
      kind: 'install' | 'update';
      code: string;
      validate: boolean;
    }) =>
      kind === 'install'
        ? unwrap(
            api.POST('/api/v1/servers/{id}/install/steam-guard', {
              params: { path: { id } },
              body: { steamGuardCode: code },
            }),
          )
        : unwrap(
            api.POST('/api/v1/servers/{id}/update', {
              params: { path: { id } },
              body: { validate, steamGuardCode: code },
            }),
          ),
    onSuccess: changed,
  });
}
