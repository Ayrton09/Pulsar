import type { PermissionInfo, PermissionScope } from './api';

/** Groups permission keys by their first segment (server, node, user…) for the checklist. */
export function groupPermissions(catalog: PermissionInfo[], scope: PermissionScope): [string, PermissionInfo[]][] {
  const groups = new Map<string, PermissionInfo[]>();
  for (const permission of catalog.filter((p) => p.grantableScopes.includes(scope))) {
    const group = permission.key.split('.')[0] ?? permission.key;
    groups.set(group, [...(groups.get(group) ?? []), permission]);
  }
  return [...groups.entries()];
}
