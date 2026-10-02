import type { Schemas } from '../api/client';

/** Permission keys used by the UI (the catalog lives in the backend: Pulsar.Domain.Access.Permissions). */
export const Permission = {
  UserView: 'user.view',
  UserManage: 'user.manage',
  RoleManage: 'role.manage',
  AuditView: 'audit.view',
  NodeView: 'node.view',
  NodeManage: 'node.manage',
  NodeCredentialsManage: 'node.credentials.manage',
  SettingsManage: 'settings.manage',
  TemplateView: 'template.view',
  ServerView: 'server.view',
  ServerCreate: 'server.create',
  ServerStart: 'server.start',
  ServerStop: 'server.stop',
  ServerRestart: 'server.restart',
  ServerKill: 'server.kill',
  ServerInstall: 'server.install',
  ServerDelete: 'server.delete',
} as const;

/**
 * True when the user holds any of the permissions. Only for hiding what the user cannot do:
 * the API is the authority and checks every request.
 */
export function can(user: Schemas['CurrentUserResponse'] | null | undefined, ...anyOf: string[]): boolean {
  return !!user && anyOf.some((permission) => user.permissions.includes(permission));
}
