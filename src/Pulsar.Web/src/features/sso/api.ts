import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { invalidateAfterChange } from '../../lib/queries';
import { adminKeys } from '../admin/api';

export type SsoKind = Schemas['SsoKind'];
export type SsoOption = Schemas['SsoOption'];
export type SsoLink = Schemas['SsoLinkView'];
export type SsoProvider = Schemas['SsoProviderView'];

export const ssoKinds: SsoKind[] = ['discord', 'google', 'oidc'];

/** Leaves the panel for the provider's page (a whole page load: the provider sends the browser back to the panel). */
export const browser = {
  go: (url: string) => window.location.assign(url),
};

export const ssoKeys = {
  options: ['sso', 'options'] as const,
  links: ['account', 'sso'] as const,
  providers: ['sso', 'providers'] as const,
};

/** The providers turned on: the sign-in page's buttons (no session needed). */
export function useSsoOptions() {
  return useQuery({ queryKey: ssoKeys.options, queryFn: () => unwrap(api.GET('/api/v1/auth/sso')), retry: false });
}

/** Goes to the provider's sign-in page: the panel keeps the flow's secrets in a cookie and answers where to go. */
export function useStartSsoLogin() {
  return useMutation({
    mutationFn: (kind: SsoKind) =>
      unwrap(api.POST('/api/v1/auth/sso/{provider}/login', { params: { path: { provider: kind } } })),
    onSuccess: ({ url }) => browser.go(url),
  });
}

export function useSsoLinks() {
  return useQuery({ queryKey: ssoKeys.links, queryFn: () => unwrap(api.GET('/api/v1/account/sso')) });
}

/** Linking takes the password (and the code with two-factor authentication on): a linked account signs in alone. */
export function useStartSsoLink() {
  return useMutation({
    mutationFn: ({ kind, ...body }: { kind: SsoKind } & Schemas['SsoLinkRequest']) =>
      unwrap(api.POST('/api/v1/account/sso/{provider}/link', { params: { path: { provider: kind } }, body })),
    onSuccess: ({ url }) => browser.go(url),
  });
}

export function useUnlinkSso() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (kind: SsoKind) =>
      unwrap(api.DELETE('/api/v1/account/sso/{provider}', { params: { path: { provider: kind } } })),
    onSettled: () => invalidateAfterChange(queryClient, { queryKey: ssoKeys.links }),
  });
}

export function useSsoProviders() {
  return useQuery({ queryKey: ssoKeys.providers, queryFn: () => unwrap(api.GET('/api/v1/sso-providers')) });
}

export function useUpdateSsoProvider() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ kind, ...body }: { kind: SsoKind } & Schemas['SsoProviderRequest']) =>
      unwrap(api.PUT('/api/v1/sso-providers/{provider}', { params: { path: { provider: kind } }, body })),
    onSuccess: () =>
      invalidateAfterChange(
        queryClient,
        { queryKey: ssoKeys.providers },
        { queryKey: ssoKeys.options },
        { queryKey: adminKeys.audit },
      ),
  });
}

/** What a provider's button and links say. */
export function ssoName(kind: SsoKind, name?: string | null): string {
  return kind === 'discord' ? 'Discord' : kind === 'google' ? 'Google' : (name ?? 'OpenID Connect');
}
