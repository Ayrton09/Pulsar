import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { invalidateAfterChange } from '../../lib/queries';
import { sessionKey } from '../auth/api';

export type User = Schemas['UserResponse'];
export type Access = Schemas['AccessReference'];
export type Role = Schemas['RoleSummary'];
export type PermissionInfo = Schemas['PermissionResponse'];
export type PermissionScope = Schemas['PermissionScope'];
export type AuditEntry = Schemas['AuditEntryView'];

export const adminKeys = {
  users: ['admin', 'users'] as const,
  roles: ['admin', 'roles'] as const,
  permissions: ['permissions'] as const,
  audit: ['admin', 'audit'] as const,
};

export function useUsers() {
  return useQuery({ queryKey: adminKeys.users, queryFn: () => unwrap(api.GET('/api/v1/users')) });
}

export function useRoles() {
  return useQuery({ queryKey: adminKeys.roles, queryFn: () => unwrap(api.GET('/api/v1/roles')) });
}

export function usePermissionCatalog() {
  return useQuery({
    queryKey: adminKeys.permissions,
    queryFn: () => unwrap(api.GET('/api/v1/permissions')),
    staleTime: Infinity,
  });
}

export function useAuditLog(pageSize = 50) {
  return useInfiniteQuery({
    queryKey: adminKeys.audit,
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) =>
      unwrap(
        api.GET('/api/v1/audit', {
          params: { query: { limit: pageSize, ...(pageParam ? { before: pageParam } : {}) } },
        }),
      ),
    getNextPageParam: (page) => page.nextBefore ?? undefined,
  });
}

/** The latest audit entries, for the dashboard (refreshed when servers or nodes change). */
export function useRecentActivity(limit: number, enabled: boolean) {
  return useQuery({
    queryKey: [...adminKeys.audit, 'recent', limit],
    queryFn: () => unwrap(api.GET('/api/v1/audit', { params: { query: { limit } } })),
    enabled,
  });
}

/**
 * Access changes touch several views: lists, assignment counts, the audit log, and possibly the signed-in user's
 * own permissions (a role they hold was edited).
 */
function useAccessChanged() {
  const queryClient = useQueryClient();
  return () =>
    invalidateAfterChange(
      queryClient,
      { queryKey: adminKeys.users },
      { queryKey: adminKeys.roles },
      { queryKey: adminKeys.audit },
      { queryKey: sessionKey },
    );
}

export function useCreateUser() {
  const changed = useAccessChanged();
  return useMutation({
    mutationFn: (body: Schemas['CreateUserRequest']) => unwrap(api.POST('/api/v1/users', { body })),
    onSuccess: changed,
  });
}

export function useUpdateUser() {
  const changed = useAccessChanged();
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: Schemas['UpdateUserRequest'] }) =>
      unwrap(api.PUT('/api/v1/users/{id}', { params: { path: { id } }, body })),
    onSuccess: changed,
  });
}

/** Gives (`granted`) or takes away a role of a user on one node or one server; answers the user as it now is. */
export function useSetUserAccess() {
  const changed = useAccessChanged();
  return useMutation({
    mutationFn: ({ id, scope, targetId, roleId, granted }: AccessChange) => {
      const path = { id, roleId, ...(scope === 'node' ? { nodeId: targetId } : { serverId: targetId }) };
      if (scope === 'node') {
        const params = { params: { path: path as { id: string; nodeId: string; roleId: string } } };
        return unwrap(
          granted
            ? api.PUT('/api/v1/users/{id}/nodes/{nodeId}/roles/{roleId}', params)
            : api.DELETE('/api/v1/users/{id}/nodes/{nodeId}/roles/{roleId}', params),
        );
      }

      const params = { params: { path: path as { id: string; serverId: string; roleId: string } } };
      return unwrap(
        granted
          ? api.PUT('/api/v1/users/{id}/servers/{serverId}/roles/{roleId}', params)
          : api.DELETE('/api/v1/users/{id}/servers/{serverId}/roles/{roleId}', params),
      );
    },
    onSuccess: changed,
  });
}

export interface AccessChange {
  id: string;
  scope: 'node' | 'server';
  targetId: string;
  roleId: string;
  granted: boolean;
}

export function useSetUserRoles() {
  const changed = useAccessChanged();
  return useMutation({
    mutationFn: ({ id, roleIds }: { id: string; roleIds: string[] }) =>
      unwrap(api.PUT('/api/v1/users/{id}/roles', { params: { path: { id } }, body: { roleIds } })),
    onSuccess: changed,
  });
}

export function useResetPassword() {
  const changed = useAccessChanged();
  return useMutation({
    mutationFn: ({ id, newPassword }: { id: string; newPassword: string }) =>
      unwrap(api.POST('/api/v1/users/{id}/password', { params: { path: { id } }, body: { newPassword } })),
    onSuccess: changed,
  });
}

export function useDeleteUser() {
  const changed = useAccessChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE('/api/v1/users/{id}', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

export function useCreateRole() {
  const changed = useAccessChanged();
  return useMutation({
    mutationFn: (body: Schemas['CreateRoleRequest']) => unwrap(api.POST('/api/v1/roles', { body })),
    onSuccess: changed,
  });
}

export function useUpdateRole() {
  const changed = useAccessChanged();
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: Schemas['UpdateRoleRequest'] }) =>
      unwrap(api.PUT('/api/v1/roles/{id}', { params: { path: { id } }, body })),
    onSuccess: changed,
  });
}

export function useDeleteRole() {
  const changed = useAccessChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE('/api/v1/roles/{id}', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

/** The user's key and recovery codes are forgotten and their sessions end (a lost phone). */
export function useResetTwoFactor() {
  const changed = useAccessChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST('/api/v1/users/{id}/two-factor/reset', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

/** Revokes every API key of a user (after a leak); answers the user as they are now. */
export function useRevokeUserApiKeys() {
  const changed = useAccessChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE('/api/v1/users/{id}/api-keys', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

/** Deletes every SSH key of a user (after a leak): the SFTP sessions they signed in end; answers the user as they are now. */
export function useDeleteUserSshKeys() {
  const changed = useAccessChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE('/api/v1/users/{id}/ssh-keys', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

export function useRequireTwoFactor() {
  const queryClient = useQueryClient();
  const changed = useAccessChanged();
  return useMutation({
    mutationFn: ({ id, required }: { id: string; required: boolean }) =>
      unwrap(api.PUT('/api/v1/roles/{id}/two-factor', { params: { path: { id } }, body: { required } })),
    // The saved role right away, so its switch does not flip back while the list is fetched again.
    onSuccess: (saved) => {
      queryClient.setQueryData<Role[]>(adminKeys.roles, (roles) => roles?.map((r) => (r.id === saved.id ? saved : r)));
      return changed();
    },
  });
}
