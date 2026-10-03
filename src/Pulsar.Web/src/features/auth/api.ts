import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';

export const sessionKey = ['session'] as const;

export type CurrentUser = Schemas['CurrentUserResponse'];
export type Session = Schemas['SessionResponse'];

/** The session also issues the antiforgery token, so it is loaded before anything else. */
export function useSession() {
  return useQuery({
    queryKey: sessionKey,
    queryFn: () => unwrap(api.GET('/api/v1/auth/session')),
    staleTime: Infinity,
  });
}

/** The signed-in user. Only use below the session guard, where it is always present. */
export function useCurrentUser(): CurrentUser {
  const { data } = useSession();
  if (!data?.user) {
    throw new Error('useCurrentUser is only valid inside the signed-in layout.');
  }
  return data.user;
}

/**
 * After sign-in, setup or sign-out the identity changed: the old cache belongs to someone else, and the session
 * must be reloaded because the antiforgery token is bound to the user.
 */
function useIdentityChange() {
  const queryClient = useQueryClient();
  return async () => {
    queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== sessionKey[0] });
    await queryClient.invalidateQueries({ queryKey: sessionKey });
  };
}

/** The password step. When the user has two-factor authentication, nothing changes until the code step. */
export function useLogin() {
  const identityChanged = useIdentityChange();
  return useMutation({
    mutationFn: (body: Schemas['LoginRequest']) => unwrap(api.POST('/api/v1/auth/login', { body })),
    onSuccess: (result) => (result.twoFactorRequired ? undefined : identityChanged()),
  });
}

/** The second step: an authenticator code or a recovery code. */
export function useLoginTwoFactor() {
  const identityChanged = useIdentityChange();
  return useMutation({
    mutationFn: (body: Schemas['TwoFactorLoginRequest']) => unwrap(api.POST('/api/v1/auth/login/two-factor', { body })),
    onSuccess: identityChanged,
  });
}

export const twoFactorKey = ['twoFactor'] as const;

export function useTwoFactorStatus() {
  return useQuery({ queryKey: twoFactorKey, queryFn: () => unwrap(api.GET('/api/v1/auth/two-factor')) });
}

/** A new key for the authenticator app. The session is renewed (new antiforgery token, read from the cookie). */
export function useTwoFactorSetup() {
  return useMutation({ mutationFn: () => unwrap(api.POST('/api/v1/auth/two-factor/setup')) });
}

/**
 * Turns two-factor authentication on and returns the recovery codes. Nothing is reloaded until the user has saved
 * them (`useSessionRefresh`): the screen showing them would otherwise give way to the new state.
 */
export function useEnableTwoFactor() {
  return useMutation({
    mutationFn: (body: Schemas['EnableTwoFactorRequest']) =>
      unwrap(api.POST('/api/v1/auth/two-factor/enable', { body })),
  });
}

export function useDisableTwoFactor() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: Schemas['PasswordConfirmationRequest']) =>
      unwrap(api.POST('/api/v1/auth/two-factor/disable', { body })),
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: twoFactorKey }),
        queryClient.invalidateQueries({ queryKey: sessionKey }),
      ]),
  });
}

export function useRenewRecoveryCodes() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: Schemas['PasswordConfirmationRequest']) =>
      unwrap(api.POST('/api/v1/auth/two-factor/recovery-codes', { body })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: twoFactorKey }),
  });
}

/** After the recovery codes were saved: the session and the status now say two-factor authentication is on. */
export function useSessionRefresh() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: sessionKey }),
      queryClient.invalidateQueries({ queryKey: twoFactorKey }),
    ]);
}

export function useSetup() {
  const identityChanged = useIdentityChange();
  return useMutation({
    mutationFn: (body: Schemas['SetupRequest']) => unwrap(api.POST('/api/v1/auth/setup', { body })),
    onSuccess: identityChanged,
  });
}

export function useUpdateProfile() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: Schemas['UpdateProfileRequest']) => unwrap(api.PUT('/api/v1/auth/profile', { body })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: sessionKey }),
  });
}

/** Other sessions of the user end; this one is renewed (with a new antiforgery token, hence the session reload). */
export function useChangePassword() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: Schemas['ChangePasswordRequest']) => unwrap(api.POST('/api/v1/auth/password', { body })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: sessionKey }),
  });
}

export function useLogout() {
  const identityChanged = useIdentityChange();
  return useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/auth/logout')),
    onSettled: identityChanged,
  });
}
