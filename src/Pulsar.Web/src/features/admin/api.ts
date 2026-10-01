import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { sessionKey } from '../auth/api';

export type User = Schemas['UserResponse'];
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

/**
 * Access changes touch several views: lists, assignment counts, the audit log, and possibly the signed-in user's
 * own permissions (a role they hold was edited).
 */
function useAccessChanged() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: adminKeys.users }),
      queryClient.invalidateQueries({ queryKey: adminKeys.roles }),
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
      queryClient.invalidateQueries({ queryKey: sessionKey }),
    ]);
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
