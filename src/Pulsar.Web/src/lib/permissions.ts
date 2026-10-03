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
  TemplateManage: 'template.manage',
  ServerView: 'server.view',
  ServerCreate: 'server.create',
  ServerStart: 'server.start',
  ServerStop: 'server.stop',
  ServerRestart: 'server.restart',
  ServerKill: 'server.kill',
  ServerConsoleView: 'server.console.view',
  ServerConsoleWrite: 'server.console.write',
  ServerFilesView: 'server.files.view',
  ServerFilesEdit: 'server.files.edit',
  ServerBackupsView: 'server.backups.view',
  ServerBackupsCreate: 'server.backups.create',
  ServerBackupsRestore: 'server.backups.restore',
  ServerBackupsDelete: 'server.backups.delete',
  ServerSchedulesView: 'server.schedules.view',
  ServerSchedulesEdit: 'server.schedules.edit',
  ServerInstall: 'server.install',
  ServerStartupEdit: 'server.startup.edit',
  ServerSettingsEdit: 'server.settings.edit',
  ServerDelete: 'server.delete',
} as const;

/**
 * True when the user holds any of the permissions. Only for hiding what the user cannot do:
 * the API is the authority and checks every request.
 */
export function can(user: Schemas['CurrentUserResponse'] | null | undefined, ...anyOf: string[]): boolean {
  return !!user && anyOf.some((permission) => user.permissions.includes(permission));
}
