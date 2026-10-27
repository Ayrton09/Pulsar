import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { adminKeys } from '../admin/api';
import { invalidateServer } from '../servers/api';

export type ModsView = Schemas['ModsView'];
export type ServerModView = Schemas['ServerModView'];
export type ModHitView = Schemas['ModHitView'];
export type ModVersionView = Schemas['ModVersionView'];
export type ModUpdateView = Schemas['ModUpdateView'];
export type ModpackView = Schemas['ModpackView'];
export type ModpackVersionView = Schemas['ModpackVersionView'];

/** A mod catalog: Modrinth, or CurseForge with the administrator's key. */
export type CatalogSource = 'modrinth' | 'curseforge' | 'umod' | 'thunderstore';

/** The catalogs' answers change slowly, and each one is a request to the catalog: they are kept a while. */
const catalogStaleTime = 5 * 60_000;

export const modKeys = {
  /** Under the server's keys: the live channel refreshes it when the server changes (a mods change completing). */
  list: (serverId: string) => ['servers', serverId, 'mods'] as const,
  /** Not under them: a server's changes do not change the catalogs, and should not ask them again. */
  catalog: (serverId: string) => ['modCatalog', serverId] as const,
  search: (serverId: string, source: string, query: string) =>
    [...modKeys.catalog(serverId), source, 'search', query] as const,
  versions: (serverId: string, source: string, projectId: string) =>
    [...modKeys.catalog(serverId), source, 'versions', projectId] as const,
  /** By the versions on the server: a change that installs others asks again. */
  updates: (serverId: string, installed: string) => [...modKeys.catalog(serverId), 'updates', installed] as const,
  modpackSearch: (serverId: string, source: string, query: string) =>
    [...modKeys.catalog(serverId), source, 'modpacks', query] as const,
  modpackVersions: (serverId: string, source: string, projectId: string) =>
    [...modKeys.catalog(serverId), source, 'modpacks', 'versions', projectId] as const,
  settings: ['modCatalogSettings'] as const,
};

export function useMods(serverId: string) {
  return useQuery({
    queryKey: modKeys.list(serverId),
    queryFn: () => unwrap(api.GET('/api/v1/servers/{id}/mods', { params: { path: { id: serverId } } })),
  });
}

/** A catalog's projects the server can take, by page; an empty query lists the most downloaded. */
export function useModSearch(serverId: string, source: CatalogSource, query: string, enabled: boolean) {
  return useInfiniteQuery({
    queryKey: modKeys.search(serverId, source, query),
    initialPageParam: 0,
    queryFn: ({ pageParam }) =>
      unwrap(
        api.GET('/api/v1/servers/{id}/mods/search', {
          params: { path: { id: serverId }, query: { source, ...(query ? { query } : {}), offset: pageParam } },
        }),
      ),
    getNextPageParam: (page, pages) => {
      const loaded = pages.reduce((count, p) => count + p.hits.length, 0);
      return page.hits.length > 0 && loaded < page.total ? loaded : undefined;
    },
    enabled,
    staleTime: catalogStaleTime,
    // A new query keeps the last results on the page until its own arrive.
    placeholderData: keepPreviousData,
  });
}

export function useModVersions(serverId: string, source: CatalogSource, projectId: string | null) {
  return useQuery({
    queryKey: modKeys.versions(serverId, source, projectId ?? ''),
    queryFn: () =>
      unwrap(
        api.GET('/api/v1/servers/{id}/mods/projects/{projectId}/versions', {
          params: { path: { id: serverId, projectId: projectId ?? '' }, query: { source } },
        }),
      ),
    enabled: projectId != null,
    staleTime: catalogStaleTime,
  });
}

/** Asked for when the user does: each check is a request to the catalogs. */
export function useModUpdates(serverId: string, installed: string, enabled: boolean) {
  return useQuery({
    queryKey: modKeys.updates(serverId, installed),
    queryFn: () => unwrap(api.GET('/api/v1/servers/{id}/mods/updates', { params: { path: { id: serverId } } })),
    enabled,
    staleTime: catalogStaleTime,
  });
}

/** A catalog's modpacks for the game's mod loaders, whatever the server's type (a pack sets it). */
export function useModpackSearch(serverId: string, source: CatalogSource, query: string, enabled: boolean) {
  return useInfiniteQuery({
    queryKey: modKeys.modpackSearch(serverId, source, query),
    initialPageParam: 0,
    queryFn: ({ pageParam }) =>
      unwrap(
        api.GET('/api/v1/servers/{id}/modpacks/search', {
          params: { path: { id: serverId }, query: { source, ...(query ? { query } : {}), offset: pageParam } },
        }),
      ),
    getNextPageParam: (page, pages) => {
      const loaded = pages.reduce((count, p) => count + p.hits.length, 0);
      return page.hits.length > 0 && loaded < page.total ? loaded : undefined;
    },
    enabled,
    staleTime: catalogStaleTime,
    placeholderData: keepPreviousData,
  });
}

/** A pack's versions, each with the server type and game version it would install (none: not for this server). */
export function useModpackVersions(serverId: string, source: CatalogSource, projectId: string | null) {
  return useQuery({
    queryKey: modKeys.modpackVersions(serverId, source, projectId ?? ''),
    queryFn: () =>
      unwrap(
        api.GET('/api/v1/servers/{id}/modpacks/projects/{projectId}/versions', {
          params: { path: { id: serverId, projectId: projectId ?? '' }, query: { source } },
        }),
      ),
    enabled: projectId != null,
    staleTime: catalogStaleTime,
  });
}

/** Each change starts an operation on the server (and is audited). */
function useModsChanged(serverId: string) {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      invalidateServer(queryClient, serverId), // the mods too: they are under its key
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
}

interface Install {
  source: CatalogSource;
  projectId: string;
  versionId?: string;
}

/** Installs the project's newest version for the server, or the one chosen, with the projects it requires. */
export function useInstallMod(serverId: string) {
  const changed = useModsChanged(serverId);
  return useMutation({
    mutationFn: ({ source, projectId, versionId }: Install) =>
      unwrap(
        api.POST('/api/v1/servers/{id}/mods', {
          params: { path: { id: serverId } },
          body: { projectId, versionId: versionId ?? null, source },
        }),
      ),
    onSettled: changed,
  });
}

/** Brings the mods named (all with a newer version, when none are) to their newest version for the server. */
export function useUpdateMods(serverId: string) {
  const changed = useModsChanged(serverId);
  return useMutation({
    mutationFn: (modIds: string[] | null) =>
      unwrap(api.POST('/api/v1/servers/{id}/mods/update', { params: { path: { id: serverId } }, body: { modIds } })),
    onSettled: changed,
  });
}

/** Installs the pack (its newest version the server can run, or the one chosen): the server is installed again as its type. */
export function useInstallModpack(serverId: string) {
  const changed = useModsChanged(serverId);
  return useMutation({
    mutationFn: ({ source, projectId, versionId }: Install) =>
      unwrap(
        api.POST('/api/v1/servers/{id}/modpacks', {
          params: { path: { id: serverId } },
          body: { projectId, versionId: versionId ?? null, source },
        }),
      ),
    onSettled: changed,
  });
}

export function useRemoveMod(serverId: string) {
  const changed = useModsChanged(serverId);
  return useMutation({
    mutationFn: (modId: string) =>
      unwrap(api.DELETE('/api/v1/servers/{id}/mods/{modId}', { params: { path: { id: serverId, modId } } })),
    onSettled: changed,
  });
}

/** Whether the panel has the administrator's CurseForge key (settings.manage); its value never comes back. */
export function useCatalogSettings() {
  return useQuery({ queryKey: modKeys.settings, queryFn: () => unwrap(api.GET('/api/v1/mod-catalogs')) });
}

/** Changing the key changes what every server's Mods tab offers. */
function useSettingsChanged() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: modKeys.settings }),
      queryClient.invalidateQueries({ queryKey: ['servers'] }),
      queryClient.invalidateQueries({ queryKey: ['modCatalog'] }),
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
}

export function useSetCurseForgeKey() {
  const changed = useSettingsChanged();
  return useMutation({
    mutationFn: (apiKey: string) => unwrap(api.PUT('/api/v1/mod-catalogs/curseforge-key', { body: { apiKey } })),
    onSuccess: changed,
  });
}

export function useRemoveCurseForgeKey() {
  const changed = useSettingsChanged();
  return useMutation({
    mutationFn: () => unwrap(api.DELETE('/api/v1/mod-catalogs/curseforge-key')),
    onSuccess: changed,
  });
}

/** A project's page on its catalog's site; Thunderstore's is in its community's (`valheim`). */
export function projectUrl(source: string, projectType: string, slug: string, community?: string | null): string {
  if (source === 'thunderstore') {
    const dash = slug.indexOf('-');
    return `https://thunderstore.io/c/${encodeURIComponent(community ?? '')}/p/${encodeURIComponent(slug.slice(0, dash))}/${encodeURIComponent(slug.slice(dash + 1))}/`;
  }
  if (source === 'umod') {
    return `https://umod.org/plugins/${encodeURIComponent(slug)}`;
  }
  if (source === 'curseforge') {
    const section = projectType === 'plugin' ? 'bukkit-plugins' : projectType === 'modpack' ? 'modpacks' : 'mc-mods';
    return `https://www.curseforge.com/minecraft/${section}/${encodeURIComponent(slug)}`;
  }
  return `https://modrinth.com/${encodeURIComponent(projectType)}/${encodeURIComponent(slug)}`;
}
