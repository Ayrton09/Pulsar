import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { adminKeys } from '../admin/api';
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

export function useSteamAccountsChanged() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: steamKeys.accounts }),
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
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

/** Runs the install or update Steam stopped again, with the Steam Guard code it asked for. */
export function useRetryWithSteamGuard() {
  const changed = useServersChanged();
  return useMutation({
    mutationFn: ({ id, kind, code }: { id: string; kind: 'install' | 'update'; code: string }) =>
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
              body: { validate: false, steamGuardCode: code },
            }),
          ),
    onSuccess: changed,
  });
}
