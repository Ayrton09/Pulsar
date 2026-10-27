import { useEffect, useId, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../../api/client';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Badge, Card, Table, Td, Th } from '../../components/ui/Display';
import { Field, Input } from '../../components/ui/Form';
import { RefreshFailed } from '../../components/ui/RefreshFailed';
import { errorMessage } from '../../lib/errors';
import { useFormatDate, useFormatFileSize } from '../../lib/format';
import { can, canOnServer, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import type { ServerDetails } from '../servers/api';
import { failureReason, serverActions } from '../servers/serverStatus';
import { Link } from 'react-router';
import {
  projectUrl,
  useInstallMod,
  useInstallModpack,
  useModpackSearch,
  useModpackVersions,
  useModSearch,
  useMods,
  useModUpdates,
  useModVersions,
  useRemoveMod,
  useUpdateMods,
  type CatalogSource,
  type ModHitView,
  type ModpackView,
  type ModUpdateView,
  type ServerModView,
} from './api';

/** A catalog's project, as the page refers to it. */
interface Project {
  source: CatalogSource;
  projectId: string;
  title: string;
}

/** How long typing pauses before the catalog is asked: each search is a request to it. */
const searchDelay = 400;

/**
 * The server's mods or plugins from a catalog (docs/architecture/07, "Mods"): Modrinth's and CurseForge's for its type and
 * game version, with the projects they require, while the server is stopped; uMod's plugins for the addon it has, while
 * it runs too. The node downloads them, checking the catalog's checksum.
 */
export function ModsTab({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const { server } = details;
  const mods = useMods(server.id);
  const install = useInstallMod(server.id);
  const update = useUpdateMods(server.id);
  const remove = useRemoveMod(server.id);
  const installPack = useInstallModpack(server.id);
  const [checking, setChecking] = useState(false);
  // A project's versions, from the catalog it is from.
  const [versionsOf, setVersionsOf] = useState<Project | null>(null);
  const [packVersionsOf, setPackVersionsOf] = useState<Project | null>(null);
  // The catalog searched: the first the server's game offers until another is chosen.
  const [chosen, setChosen] = useState<CatalogSource | null>(null);
  const canChange = canOnServer(me, server, Permission.ServerInstall);
  // The same moment a reinstall is possible: connected, idle and stopped; and installed, as mods go into the game. A game
  // that loads its catalog's files as they come (uMod's plugins) takes them while it runs too.
  const changeable =
    server.installState === 'installed' &&
    (mods.data?.whileRunning === true
      ? server.nodeConnected && server.activeOperation === null
      : serverActions(server).reinstall);
  const installed = mods.data?.installed ?? [];
  const usable = mods.data?.supported === true && mods.data.unavailable == null;
  const updates = useModUpdates(
    server.id,
    installed
      .map((mod) => mod.versionId)
      .sort()
      .join(','),
    checking && usable && installed.length > 0,
  );

  if (mods.isPending) {
    return <Spinner label={t('common.loading')} />;
  }
  // A failed refetch (after a reconnect, say) keeps the tab with the last data, and what is open on it.
  if (!mods.data) {
    return <Alert>{errorMessage(t, mods.error)}</Alert>;
  }

  const view = mods.data;
  const plugins = view.projectType === 'plugin';
  const catalogs = view.catalogs as CatalogSource[];
  const source: CatalogSource | undefined = chosen && catalogs.includes(chosen) ? chosen : catalogs[0];
  // A change on its way (rows still installing or removing, a pack installing) ends before another starts.
  const idle =
    changeable &&
    !install.isPending &&
    !update.isPending &&
    !remove.isPending &&
    !installPack.isPending &&
    view.modpackInstalling == null &&
    installed.every((mod) => mod.state === 'installed');
  const enabled = idle && usable;
  const error = install.error ?? update.error ?? remove.error ?? installPack.error;
  // The node's refusals come when the operation ends: the last change says why it failed, until another one works.
  const lastChange = details.recentOperations.find(
    (operation) => operation.type === 'modsChange' || operation.type === 'modpackInstall',
  );
  const lastFailed = lastChange && (lastChange.status === 'failed' || lastChange.status === 'interrupted');
  const newer = new Map((updates.data?.updates ?? []).map((u) => [u.modId, u]));
  // The panel's refusal names the mods in English: say it again with the names, in the user's language.
  const refusal = (): string | null => {
    const mod = installed.find((m) => m.id === remove.variables);
    return remove.error instanceof ApiError &&
      remove.error.code === 'mods.required_by' &&
      mod &&
      mod.requiredBy.length > 0
      ? t('mods.refused.required_by', { name: mod.name, others: mod.requiredBy.join(', ') })
      : null;
  };
  // Each change clears the others' errors: the alert is about the last one.
  const reset = () => {
    install.reset();
    update.reset();
    remove.reset();
    installPack.reset();
  };
  const installMod = (project: Project, versionId?: string) => {
    reset();
    install.mutate(
      versionId
        ? { source: project.source, projectId: project.projectId, versionId }
        : { source: project.source, projectId: project.projectId },
      { onSuccess: () => setVersionsOf(null) },
    );
  };
  const updateMods = (modIds: string[] | null) => {
    reset();
    update.mutate(modIds);
  };
  const installModpack = (project: Project, versionId?: string) => {
    if (window.confirm(t('mods.modpacks.installConfirm', { name: project.title, server: server.name }))) {
      reset();
      installPack.mutate(
        versionId
          ? { source: project.source, projectId: project.projectId, versionId }
          : { source: project.source, projectId: project.projectId },
        { onSuccess: () => setPackVersionsOf(null) },
      );
    }
  };
  const removeMod = (mod: ServerModView) => {
    if (window.confirm(t('mods.removeConfirm', { name: mod.name, server: server.name }))) {
      reset();
      remove.mutate(mod.id);
    }
  };

  return (
    <div className="space-y-6">
      {mods.isError && <RefreshFailed error={mods.error} retry={() => void mods.refetch()} />}
      <p className="max-w-2xl text-sm text-muted">
        {t(
          source === 'umod' || source === 'thunderstore'
            ? `mods.hint.${source}`
            : plugins
              ? 'mods.hint.plugin'
              : 'mods.hint.mod',
          {
            version: view.gameVersion ?? '?',
          },
        )}
      </p>
      {view.unavailable && <Alert>{t(`errors.${view.unavailable}`, { defaultValue: view.unavailable })}</Alert>}
      {canChange && (usable || view.modpacks) && !changeable && (
        <p className="text-sm text-muted">{t('mods.stopToChange')}</p>
      )}
      {error && <Alert>{refusal() ?? errorMessage(t, error)}</Alert>}
      {!error && lastFailed && (
        <Alert>
          {t('mods.lastFailed', { reason: failureReason(t, lastChange.errorCode, lastChange.errorMessage) })}
        </Alert>
      )}
      <section className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="text-lg font-semibold">{t('mods.installed.title')}</h2>
          {canChange && usable && installed.length > 0 && (
            <div className="flex flex-wrap items-center gap-2">
              {checking && updates.data && (
                <span className="text-sm text-muted">
                  {newer.size === 0 ? t('mods.updates.none') : t('mods.updates.found', { count: newer.size })}
                </span>
              )}
              {checking && newer.size > 0 ? (
                <Button
                  disabled={!enabled}
                  busy={update.isPending && update.variables === null}
                  onClick={() => updateMods(null)}
                >
                  {t('mods.updates.all')}
                </Button>
              ) : (
                <Button
                  variant="secondary"
                  busy={updates.isFetching}
                  onClick={() => (checking ? void updates.refetch() : setChecking(true))}
                >
                  {t('mods.updates.check')}
                </Button>
              )}
            </div>
          )}
        </div>
        {updates.isError && <Alert>{errorMessage(t, updates.error)}</Alert>}
        {view.modpack && <p className="max-w-2xl text-sm text-muted">{t('mods.modpacks.notListed')}</p>}
        {installed.length === 0 ? (
          <p className="text-sm text-muted">{t(plugins ? 'mods.installed.noPlugins' : 'mods.installed.noMods')}</p>
        ) : (
          <InstalledTable
            installed={installed}
            newer={newer}
            canChange={canChange}
            enabled={enabled}
            busy={(mod) =>
              (remove.isPending && remove.variables === mod.id) ||
              (update.isPending && update.variables?.includes(mod.id) === true)
            }
            onVersions={(mod) =>
              setVersionsOf({ source: mod.source as CatalogSource, projectId: mod.projectId, title: mod.name })
            }
            onUpdate={(mod) => updateMods([mod.id])}
            onRemove={removeMod}
          />
        )}
      </section>
      {canChange && view.catalogsNeedingKey.length > 0 && (
        <p className="max-w-2xl text-sm text-muted">
          {can(me, Permission.SettingsManage) ? (
            <>
              {t('mods.catalogs.needsKey')}{' '}
              <Link to="/templates" className="text-accent hover:underline">
                {t('mods.catalogs.setKey')}
              </Link>
            </>
          ) : (
            t('mods.catalogs.needsKeyAskAdmin')
          )}
        </p>
      )}
      {canChange && (usable || view.modpacks) && catalogs.length > 1 && source && (
        <CatalogSwitch catalogs={catalogs} chosen={source} onChoose={setChosen} />
      )}
      {canChange && usable && source && (
        <Catalog
          key={`mods-${source}`}
          kind="mods"
          source={source}
          community={view.community ?? null}
          serverId={server.id}
          projectType={view.projectType ?? 'mod'}
          onServer={new Set(installed.filter((mod) => mod.source === source).map((mod) => mod.projectId))}
          enabled={enabled}
          installing={install.isPending ? (install.variables?.projectId ?? null) : null}
          onInstall={(hit) => installMod({ source, projectId: hit.projectId, title: hit.title })}
          onVersions={(hit) => setVersionsOf({ source, projectId: hit.projectId, title: hit.title })}
        />
      )}
      {view.modpacks && (
        <section className="space-y-3">
          <h2 className="text-lg font-semibold">{t('mods.modpacks.title')}</h2>
          <p className="max-w-2xl text-sm text-muted">{t('mods.modpacks.hint')}</p>
          <ModpackCard pack={view.modpack ?? null} installing={view.modpackInstalling ?? null} />
          {canChange && source && (
            <Catalog
              key={`modpacks-${source}`}
              kind="modpacks"
              source={source}
              community={view.community ?? null}
              serverId={server.id}
              projectType="modpack"
              onServer={new Set(view.modpack?.source === source ? [view.modpack.projectId] : [])}
              enabled={idle}
              installing={installPack.isPending ? (installPack.variables?.projectId ?? null) : null}
              onInstall={(hit) => installModpack({ source, projectId: hit.projectId, title: hit.title })}
              onVersions={(hit) => setPackVersionsOf({ source, projectId: hit.projectId, title: hit.title })}
            />
          )}
        </section>
      )}
      {packVersionsOf && (
        <ModpackVersionsDialog
          serverId={server.id}
          project={packVersionsOf}
          current={view.modpack?.source === packVersionsOf.source ? view.modpack.versionId : null}
          enabled={idle}
          installing={installPack.isPending ? (installPack.variables?.versionId ?? null) : null}
          error={installPack.error}
          onInstall={(versionId) => installModpack(packVersionsOf, versionId)}
          onClose={() => setPackVersionsOf(null)}
        />
      )}
      {versionsOf && (
        <VersionsDialog
          serverId={server.id}
          project={versionsOf}
          installed={installed}
          enabled={enabled}
          installing={install.isPending ? (install.variables?.versionId ?? null) : null}
          error={install.error}
          onInstall={(versionId) => installMod(versionsOf, versionId)}
          onClose={() => setVersionsOf(null)}
        />
      )}
    </div>
  );
}

function InstalledTable({
  installed,
  newer,
  canChange,
  enabled,
  busy,
  onVersions,
  onUpdate,
  onRemove,
}: {
  installed: ServerModView[];
  newer: Map<string, ModUpdateView>;
  canChange: boolean;
  enabled: boolean;
  busy: (mod: ServerModView) => boolean;
  onVersions: (mod: ServerModView) => void;
  onUpdate: (mod: ServerModView) => void;
  onRemove: (mod: ServerModView) => void;
}) {
  const { t } = useTranslation();
  const formatSize = useFormatFileSize();
  return (
    <Table label={t('mods.installed.label')}>
      <thead>
        <tr>
          <Th>{t('mods.columns.name')}</Th>
          <Th>{t('mods.columns.version')}</Th>
          <Th className="hidden md:table-cell">{t('mods.columns.file')}</Th>
          {canChange && (
            <Th>
              <span className="sr-only">{t('mods.columns.actions')}</span>
            </Th>
          )}
        </tr>
      </thead>
      <tbody>
        {installed.map((mod) => {
          const update = newer.get(mod.id);
          return (
            <tr key={mod.id}>
              <Td>
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-medium">{mod.name}</span>
                  {mod.state !== 'installed' && <Badge tone="accent">{t(`mods.states.${mod.state}`)}</Badge>}
                  {mod.dependency && <Badge>{t('mods.dependency')}</Badge>}
                  {mod.source !== 'modrinth' && (
                    <span className="text-xs text-muted">
                      {t(`mods.catalogs.${mod.source}`, { defaultValue: mod.source })}
                    </span>
                  )}
                </div>
                {mod.requiredBy.length > 0 && (
                  <p className="mt-1 text-xs text-muted">
                    {t('mods.requiredBy', { names: mod.requiredBy.join(', ') })}
                  </p>
                )}
              </Td>
              <Td>
                <span className="break-all">{mod.versionNumber}</span>
                {update && (
                  <p className="mt-1 text-xs break-all text-success">
                    {t('mods.updates.to', { version: update.newVersionNumber })}
                  </p>
                )}
              </Td>
              <Td className="hidden text-xs text-muted md:table-cell">
                <span className="break-all">{mod.path}</span>
                {/* uMod does not say how large a plugin is. */}
                {mod.size > 0 && <span className="block">{formatSize(mod.size)}</span>}
              </Td>
              {canChange && (
                <Td>
                  <div className="flex flex-wrap justify-end gap-2">
                    {update && (
                      <Button size="sm" disabled={!enabled} busy={busy(mod)} onClick={() => onUpdate(mod)}>
                        {t('mods.update')}
                      </Button>
                    )}
                    <Button size="sm" variant="ghost" disabled={!enabled} onClick={() => onVersions(mod)}>
                      {t('mods.versions.open')}
                    </Button>
                    <Button
                      size="sm"
                      variant="dangerSubtle"
                      disabled={!enabled}
                      busy={!update && busy(mod)}
                      onClick={() => onRemove(mod)}
                    >
                      {t('mods.remove')}
                    </Button>
                  </div>
                </Td>
              )}
            </tr>
          );
        })}
      </tbody>
    </Table>
  );
}

/** The catalog's search: mods or plugins for the server's type and game version, or modpacks for any of its mod types. */
function Catalog({
  kind,
  source,
  community,
  serverId,
  projectType,
  onServer,
  enabled,
  installing,
  onInstall,
  onVersions,
}: {
  kind: 'mods' | 'modpacks';
  source: CatalogSource;
  community: string | null;
  serverId: string;
  projectType: string;
  /** From the server's list, not the hits: it is current after a change, the search may be minutes old. */
  onServer: Set<string>;
  enabled: boolean;
  installing: string | null;
  onInstall: (hit: ModHitView) => void;
  onVersions: (hit: ModHitView) => void;
}) {
  const { t } = useTranslation();
  const [text, setText] = useState('');
  const [query, setQuery] = useState('');
  useEffect(() => {
    const timer = setTimeout(() => setQuery(text.trim()), searchDelay);
    return () => clearTimeout(timer);
  }, [text]);
  const mods = useModSearch(serverId, source, query, kind === 'mods');
  const modpacks = useModpackSearch(serverId, source, query, kind === 'modpacks');
  const search = kind === 'mods' ? mods : modpacks;
  const hits = search.data?.pages.flatMap((page) => page.hits) ?? [];
  const label =
    kind === 'modpacks'
      ? 'mods.modpacks.search'
      : projectType === 'plugin'
        ? 'mods.catalog.searchPlugins'
        : 'mods.catalog.searchMods';

  return (
    <section className="space-y-3">
      {kind === 'mods' && <h2 className="text-lg font-semibold">{t(`mods.catalogs.${source}`)}</h2>}
      <Field label={t(label)}>
        {(control) => (
          <Input {...control} type="search" maxLength={100} value={text} onChange={(e) => setText(e.target.value)} />
        )}
      </Field>
      {search.isError && <Alert>{errorMessage(t, search.error)}</Alert>}
      {search.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : hits.length === 0 ? (
        !search.isError && <p className="text-sm text-muted">{t('mods.catalog.empty')}</p>
      ) : (
        <ul className="grid grid-cols-1 gap-3 xl:grid-cols-2" aria-busy={search.isPlaceholderData || undefined}>
          {hits.map((hit) => (
            <HitCard
              key={hit.projectId}
              source={source}
              community={community}
              hit={hit}
              projectType={projectType}
              installed={onServer.has(hit.projectId)}
              enabled={enabled}
              installing={installing === hit.projectId}
              onInstall={() => onInstall(hit)}
              onVersions={() => onVersions(hit)}
            />
          ))}
        </ul>
      )}
      {search.hasNextPage && (
        <Button variant="secondary" busy={search.isFetchingNextPage} onClick={() => void search.fetchNextPage()}>
          {t('mods.catalog.more')}
        </Button>
      )}
    </section>
  );
}

function HitCard({
  source,
  community,
  hit,
  projectType,
  installed,
  enabled,
  installing,
  onInstall,
  onVersions,
}: {
  source: CatalogSource;
  community: string | null;
  hit: ModHitView;
  projectType: string;
  installed: boolean;
  enabled: boolean;
  installing: boolean;
  onInstall: () => void;
  onVersions: () => void;
}) {
  const { t, i18n } = useTranslation();
  const noteId = useId();
  const downloads = useMemo(() => new Intl.NumberFormat(i18n.language, { notation: 'compact' }), [i18n.language]);
  return (
    <li>
      <Card className="flex h-full flex-col gap-2 p-4">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="min-w-0">
            <h3 className="font-semibold break-words">
              <a
                href={projectUrl(source, projectType, hit.slug, community)}
                target="_blank"
                rel="noreferrer noopener"
                aria-describedby={noteId}
                className="hover:text-accent hover:underline"
              >
                {hit.title}
              </a>
            </h3>
            <span id={noteId} className="sr-only">
              {t('common.opensInNewTab')}
            </span>
            <p className="text-xs text-muted">
              {hit.author ? t('mods.catalog.by', { author: hit.author }) : null}
              {hit.author ? ' · ' : null}
              {t('mods.catalog.downloads', { count: hit.downloads, formatted: downloads.format(hit.downloads) })}
            </p>
          </div>
          {installed && <Badge tone="success">{t('mods.onServer')}</Badge>}
        </div>
        {hit.description && <p className="text-sm break-words">{hit.description}</p>}
        <div className="mt-auto flex flex-wrap gap-2 pt-1">
          {!installed && (
            <Button size="sm" disabled={!enabled} busy={installing} onClick={onInstall}>
              {t('mods.install')}
            </Button>
          )}
          <Button size="sm" variant="ghost" disabled={!enabled} onClick={onVersions}>
            {t('mods.versions.open')}
          </Button>
        </div>
      </Card>
    </li>
  );
}

function VersionsDialog({
  serverId,
  project,
  installed,
  enabled,
  installing,
  error,
  onInstall,
  onClose,
}: {
  serverId: string;
  project: Project;
  installed: ServerModView[];
  enabled: boolean;
  installing: string | null;
  error: Error | null;
  onInstall: (versionId: string) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const versions = useModVersions(serverId, project.source, project.projectId);
  const current = installed.find((mod) => mod.projectId === project.projectId)?.versionId;
  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={t('mods.versions.title', { name: project.title })}
      description={t('mods.versions.hint')}
      size="lg"
    >
      {error && <Alert>{errorMessage(t, error)}</Alert>}
      {versions.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : versions.isError ? (
        <Alert>{errorMessage(t, versions.error)}</Alert>
      ) : versions.data.versions.length === 0 ? (
        <p className="text-sm text-muted">{t('errors.mods.no_version')}</p>
      ) : (
        <ul className="divide-y divide-border">
          {versions.data.versions.map((version) => (
            <li key={version.id} className="flex flex-wrap items-center justify-between gap-2 py-3">
              <div className="min-w-0">
                <p className="flex flex-wrap items-center gap-2 font-medium">
                  <span className="break-all">{version.versionNumber}</span>
                  {version.versionType !== 'release' && (
                    <Badge tone="accent">
                      {t(`mods.versions.types.${version.versionType}`, { defaultValue: version.versionType })}
                    </Badge>
                  )}
                </p>
                <p className="text-xs text-muted">
                  {version.name !== version.versionNumber ? `${version.name} · ` : ''}
                  {formatDate(version.published)}
                </p>
              </div>
              {version.id === current ? (
                <Badge tone="success">{t('mods.onServer')}</Badge>
              ) : (
                <Button
                  size="sm"
                  variant={current ? 'secondary' : 'primary'}
                  disabled={!enabled}
                  busy={installing === version.id}
                  onClick={() => onInstall(version.id)}
                >
                  {current ? t('mods.versions.change') : t('mods.install')}
                </Button>
              )}
            </li>
          ))}
        </ul>
      )}
    </Dialog>
  );
}

/** The pack on the server, and the one being installed. */
function ModpackCard({ pack, installing }: { pack: ModpackView | null; installing: ModpackView | null }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  if (!pack && !installing) {
    return <p className="text-sm text-muted">{t('mods.modpacks.none')}</p>;
  }

  return (
    <Card className="space-y-2 p-4">
      {pack && (
        <div>
          <p className="flex flex-wrap items-center gap-2 font-medium">
            <span className="break-words">{pack.name}</span>
            <span className="text-muted">{pack.versionNumber}</span>
            <span className="text-xs text-muted">
              {t(`mods.catalogs.${pack.source}`, { defaultValue: pack.source })}
            </span>
          </p>
          <p className="text-xs text-muted">
            {t('mods.modpacks.runsOn', {
              loader: t(`mods.modpacks.loaders.${pack.loader}`, { defaultValue: pack.loader }),
              loaderVersion: pack.loaderVersion ?? '',
              gameVersion: pack.gameVersion,
            })}
            {' · '}
            {t('mods.modpacks.installedAt', { when: formatDate(pack.installedAt) })}
          </p>
        </div>
      )}
      {installing && <Badge tone="accent">{t('mods.modpacks.installing', { name: installing.name })}</Badge>}
    </Card>
  );
}

function ModpackVersionsDialog({
  serverId,
  project,
  current,
  enabled,
  installing,
  error,
  onInstall,
  onClose,
}: {
  serverId: string;
  project: Project;
  current: string | null;
  enabled: boolean;
  installing: string | null;
  error: Error | null;
  onInstall: (versionId: string) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const versions = useModpackVersions(serverId, project.source, project.projectId);
  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={t('mods.versions.title', { name: project.title })}
      description={t('mods.modpacks.versionsHint')}
      size="lg"
    >
      {error && <Alert>{errorMessage(t, error)}</Alert>}
      {versions.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : versions.isError ? (
        <Alert>{errorMessage(t, versions.error)}</Alert>
      ) : versions.data.versions.length === 0 ? (
        <p className="text-sm text-muted">{t('errors.modpack.no_version')}</p>
      ) : (
        <ul className="divide-y divide-border">
          {versions.data.versions.map((version) => (
            <li key={version.id} className="flex flex-wrap items-center justify-between gap-2 py-3">
              <div className="min-w-0">
                <p className="flex flex-wrap items-center gap-2 font-medium">
                  <span className="break-all">{version.versionNumber}</span>
                  {version.versionType !== 'release' && (
                    <Badge tone="accent">
                      {t(`mods.versions.types.${version.versionType}`, { defaultValue: version.versionType })}
                    </Badge>
                  )}
                </p>
                <p className="text-xs text-muted">
                  {version.serverType && version.gameVersion
                    ? t('mods.modpacks.versionRunsOn', {
                        loader: t(`mods.modpacks.loaders.${version.loaders[0] ?? ''}`, {
                          defaultValue: version.loaders.join(', '),
                        }),
                        gameVersion: version.gameVersion,
                      })
                    : t('mods.modpacks.notForServer', {
                        loaders: version.loaders.join(', '),
                        gameVersions: version.gameVersions.join(', '),
                      })}
                  {' · '}
                  {formatDate(version.published)}
                </p>
              </div>
              {version.id === current ? (
                <Badge tone="success">{t('mods.onServer')}</Badge>
              ) : (
                version.serverType && (
                  <Button
                    size="sm"
                    variant={current ? 'secondary' : 'primary'}
                    disabled={!enabled}
                    busy={installing === version.id}
                    onClick={() => onInstall(version.id)}
                  >
                    {current ? t('mods.versions.change') : t('mods.install')}
                  </Button>
                )
              )}
            </li>
          ))}
        </ul>
      )}
    </Dialog>
  );
}

/** Which catalog the searches ask, when the server's game takes more than one. */
function CatalogSwitch({
  catalogs,
  chosen,
  onChoose,
}: {
  catalogs: CatalogSource[];
  chosen: CatalogSource;
  onChoose: (source: CatalogSource) => void;
}) {
  const { t } = useTranslation();
  return (
    <div role="group" aria-label={t('mods.catalogs.label')} className="flex flex-wrap gap-2">
      {catalogs.map((source) => (
        <Button
          key={source}
          size="sm"
          variant={source === chosen ? 'primary' : 'secondary'}
          aria-pressed={source === chosen}
          onClick={() => onChoose(source)}
        >
          {t(`mods.catalogs.${source}`)}
        </Button>
      ))}
    </div>
  );
}
