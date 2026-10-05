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
  ServerLimitsEdit: 'server.limits.edit',
  ServerStartupEdit: 'server.startup.edit',
  ServerSettingsEdit: 'server.settings.edit',
  ServerDelete: 'server.delete',
} as const;

type CurrentUser = Schemas['CurrentUserResponse'] | null | undefined;

/**
 * True when the user holds any of the permissions globally (everywhere). Only for hiding what the user cannot do: the
 * API is the authority and checks every request. For a server or a node, use `canOnServer` and `canOnNode`, which also
 * count roles given on them.
 */
export function can(user: CurrentUser, ...anyOf: string[]): boolean {
  return !!user && anyOf.some((permission) => user.permissions.includes(permission));
}

/** True when the user holds any of the permissions on this node: globally or through a role on it. */
export function canOnNode(user: CurrentUser, nodeId: string, ...anyOf: string[]): boolean {
  const onNode = user?.nodePermissions?.find((grant) => grant.id === nodeId)?.permissions ?? [];
  return can(user, ...anyOf) || anyOf.some((permission) => onNode.includes(permission));
}

/** True when the user holds any of the permissions on this server: globally, through its node, or through a role on it. */
export function canOnServer(user: CurrentUser, server: { id: string; nodeId: string }, ...anyOf: string[]): boolean {
  const onServer = user?.serverPermissions?.find((grant) => grant.id === server.id)?.permissions ?? [];
  return canOnNode(user, server.nodeId, ...anyOf) || anyOf.some((permission) => onServer.includes(permission));
}

/**
 * True when the user holds any of the permissions somewhere: globally, or on some node or server. For navigation and
 * lists, which then show only what the permission applies to.
 */
export function canAnywhere(user: CurrentUser, ...anyOf: string[]): boolean {
  const scoped = [...(user?.nodePermissions ?? []), ...(user?.serverPermissions ?? [])];
  return (
    can(user, ...anyOf) || scoped.some((grant) => anyOf.some((permission) => grant.permissions.includes(permission)))
  );
}
