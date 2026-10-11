import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { ApiError } from '../../api/client';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Badge, Card, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { Checkbox } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatBytes, useFormatDate, useFormatDuration } from '../../lib/format';
import { canOnServer, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { AddonsTab } from '../addons/AddonsTab';
import { BackupsTab } from '../backups/BackupsTab';
import { DatabasesTab } from '../databases/DatabasesTab';
import { SteamGuardPrompt } from '../steam/SteamServerCards';
import { FilesTab } from '../files/FilesTab';
import { SchedulesTab } from '../schedules/SchedulesTab';
import { useServerMetrics, useWatchServers } from '../live/hooks';
import { ServerUsageHistory } from '../metrics/UsageHistory';
import type { ServerMetrics, ServerQuery } from '../live/types';
import {
  useDeleteServer,
  useReinstallServer,
  useServer,
  useServerCommand,
  useUpdateGame,
  useUpgradeTemplate,
  type OperationView,
  type ServerCommand,
  type ServerDetails,
} from './api';
import { NodeDisconnectedBadge, ServerStateBadge, StateReason } from './components';
import { ConsoleTab } from './ConsoleTab';
import { NetworkTab, OperationProgress, SettingsTab } from './ServerTabs';
import { commandRefusal, failureReason, serverActions } from './serverStatus';

const allTabs = [
  'overview',
  'console',
  'files',
  'addons',
  'backups',
  'databases',
  'schedules',
  'settings',
  'network',
] as const;
type Tab = (typeof allTabs)[number];

/** One server: its state and live usage, the lifecycle actions, its console and its recent operations. */
export function ServerPage() {
  const { t } = useTranslation();
  const { id = '' } = useParams();
  const me = useCurrentUser();
  const details = useServer(id);
  useWatchServers();
  const metrics = useServerMetrics(id);
  const [search, setSearch] = useSearchParams();
  // Until the server is loaded nothing is shown, tabs included.
  const target = details.data?.server ?? { id, nodeId: '' };
  const tabs = allTabs.filter(
    (name) =>
      (name !== 'console' || canOnServer(me, target, Permission.ServerConsoleView)) &&
      (name !== 'files' || canOnServer(me, target, Permission.ServerFilesView)) &&
      // Only for games whose template has addons.
      (name !== 'addons' || details.data?.hasAddons === true) &&
      (name !== 'backups' || canOnServer(me, target, Permission.ServerBackupsView)) &&
      (name !== 'databases' || canOnServer(me, target, Permission.ServerDatabasesView)) &&
      (name !== 'schedules' || canOnServer(me, target, Permission.ServerSchedulesView)),
  );
  const requested = search.get('tab');
  const tab: Tab = tabs.find((name) => name === requested) ?? 'overview';
  // The Files tab's folder and open file stay in the address: going back to it finds them again. Leaving an editor
  // with unsaved changes asks first (FileEditor).
  const openTab = (name: Tab) => {
    const next = new URLSearchParams();
    if (name !== 'overview') {
      next.set('tab', name);
    }
    for (const key of ['path', 'file']) {
      const value = search.get(key);
      if (value !== null) {
        next.set(key, value);
      }
    }
    setSearch(next, { replace: true });
  };

  if (details.isPending) {
    return <Spinner label={t('common.loading')} />;
  }
  // A failed refresh keeps the page with what was loaded last (an open editor included): only a first load that
  // failed has nothing to show. A server deleted meanwhile, or no longer this user's to see, is gone, though.
  const gone =
    details.error instanceof ApiError &&
    (details.error.code === 'server.not_found' || details.error.code === 'auth.forbidden');
  if (details.data === undefined || gone) {
    return gone ? (
      <div className="py-16 text-center">
        <h1 className="text-xl font-semibold">{t('servers.notFound')}</h1>
        <Link to="/servers" className="mt-6 inline-block text-sm text-accent hover:underline">
          {t('servers.backToList')}
        </Link>
      </div>
    ) : (
      <Alert>{errorMessage(t, details.error)}</Alert>
    );
  }

  const { server } = details.data;
  return (
    <>
      <p className="mb-2 text-sm">
        <Link to="/servers" className="text-muted hover:text-text">
          ← {t('servers.title')}
        </Link>
      </p>
      {details.isError && (
        <div className="mb-4">
          <Alert>{t('servers.stale', { error: errorMessage(t, details.error) })}</Alert>
        </div>
      )}
      <PageHeader
        title={server.name}
        description={details.data.description ?? undefined}
        actions={
          <>
            {!server.nodeConnected && <NodeDisconnectedBadge />}
            <ServerStateBadge server={server} />
          </>
        }
      />
      {(server.runtimeState === 'starting' || server.runtimeState === 'running') && metrics && (
        <ServerUsage metrics={metrics} limits={details.data.limits} />
      )}
      <LifecycleActions details={details.data} />
      {server.activeOperation && (
        <div className="mt-4">
          <OperationProgress server={server} operation={server.activeOperation} />
        </div>
      )}
      <SteamGuardPrompt details={details.data} />
      <div role="tablist" aria-label={t('servers.tabs.label')} className="mt-6 flex gap-1 border-b border-border">
        {tabs.map((name) => (
          <button
            key={name}
            type="button"
            role="tab"
            aria-selected={tab === name}
            onClick={() => openTab(name)}
            className={`-mb-px border-b-2 px-4 py-2 text-sm ${tab === name ? 'border-accent text-accent' : 'border-transparent text-muted hover:text-text'}`}
          >
            {t(`servers.tabs.${name}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel" className="mt-6">
        {tab === 'overview' && (
          <>
            <div className="grid gap-6 xl:grid-cols-2">
              <OverviewCard details={details.data} />
              {server.runtimeState === 'running' && metrics?.query && <PlayersCard query={metrics.query} />}
              <OperationsCard operations={details.data.recentOperations} />
            </div>
            <ServerUsageHistory serverId={server.id} />
            <AdministrationSection details={details.data} />
          </>
        )}
        {tab === 'console' && <ConsoleTab details={details.data} />}
        {tab === 'files' && <FilesTab details={details.data} />}
        {tab === 'addons' && <AddonsTab details={details.data} />}
        {tab === 'backups' && <BackupsTab details={details.data} />}
        {tab === 'databases' && <DatabasesTab details={details.data} />}
        {tab === 'schedules' && <SchedulesTab details={details.data} />}
        {tab === 'settings' && <SettingsTab details={details.data} />}
        {tab === 'network' && <NetworkTab details={details.data} />}
      </div>
    </>
  );
}

/** CPU, memory and uptime of the running server, and its players when it answers queries, pushed every few seconds. */
function ServerUsage({ metrics, limits }: { metrics: ServerMetrics; limits: ServerDetails['limits'] }) {
  const { t } = useTranslation();
  const formatBytes = useFormatBytes();
  const formatDuration = useFormatDuration();
  const { query } = metrics;
  const cpu = t('servers.usage.cpuValue', { value: metrics.cpuPercent });
  // With a memory limit, what it counts (committed memory) next to it; otherwise the working set, as Task Manager shows.
  const memoryLimit = limits.memoryMb == null ? null : limits.memoryMb * 1024 * 1024;
  const items: [string, string][] = [
    [
      t('servers.usage.cpu'),
      limits.cpuPercent == null
        ? cpu
        : t('servers.usage.ofLimit', { value: cpu, limit: t('servers.usage.cpuLimit', { value: limits.cpuPercent }) }),
    ],
    [
      t('servers.usage.memory'),
      memoryLimit == null
        ? formatBytes(metrics.memoryBytes)
        : t('servers.usage.ofLimit', {
            value: formatBytes(metrics.committedBytes ?? metrics.memoryBytes),
            limit: formatBytes(memoryLimit),
          }),
    ],
    [t('servers.usage.uptime'), formatDuration(metrics.uptimeSeconds)],
  ];
  if (query) {
    const players = t('servers.usage.playersValue', { players: query.players, max: query.maxPlayers });
    items.push([
      t('servers.usage.players'),
      query.bots > 0 ? `${players} ${t('servers.usage.bots', { count: query.bots })}` : players,
    ]);
    if (query.map) {
      items.push([t('servers.usage.map'), query.map]);
    }
  }
  return (
    <dl aria-label={t('servers.usage.label')} className="mb-4 flex flex-wrap gap-x-8 gap-y-2 text-sm">
      {items.map(([label, value]) => (
        <div key={label}>
          <dt className="text-xs text-muted">{label}</dt>
          <dd className="font-medium tabular-nums">{value}</dd>
        </div>
      ))}
    </dl>
  );
}

function LifecycleActions({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const command = useServerCommand();
  const { server } = details;
  const available = serverActions(server);
  const run = (name: ServerCommand) => command.mutate({ id: server.id, command: name });
  const buttons: [ServerCommand, string, boolean][] = [
    ['start', Permission.ServerStart, available.start],
    ['stop', Permission.ServerStop, available.stop],
    ['restart', Permission.ServerRestart, available.restart],
    ['kill', Permission.ServerKill, available.kill],
  ];
  const visible = buttons.filter(([, permission]) => canOnServer(me, server, permission));
  if (visible.length === 0) {
    return null;
  }

  const refused = command.data && command.data.status !== 'accepted' && command.data.status !== 'alreadyInState';
  return (
    <section aria-label={t('servers.actions')} className="space-y-2">
      <div className="flex flex-wrap gap-2">
        {visible.map(([name, , enabled]) => (
          <Button
            key={name}
            variant={name === 'kill' ? 'danger' : name === 'start' ? 'primary' : 'secondary'}
            disabled={!enabled || command.isPending}
            busy={command.isPending && command.variables.command === name}
            onClick={() =>
              (name !== 'kill' || window.confirm(t('servers.killConfirm', { name: server.name }))) && run(name)
            }
          >
            {t(`servers.commands.${name}`)}
          </Button>
        ))}
      </div>
      {command.isError && <Alert>{errorMessage(t, command.error)}</Alert>}
      {refused && (
        <p role="status" className="text-sm text-danger">
          {commandRefusal(t, command.data.status, command.data.errorCode)}
        </p>
      )}
    </section>
  );
}

function Facts({ items }: { items: [string, ReactNode][] }) {
  return (
    <dl className="grid grid-cols-[minmax(8rem,auto)_1fr] gap-x-4 gap-y-2 text-sm">
      {items.map(([label, value]) => (
        <div key={label} className="contents">
          <dt className="text-muted">{label}</dt>
          <dd className="min-w-0 break-words">{value}</dd>
        </div>
      ))}
    </dl>
  );
}

function OverviewCard({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const { server } = details;
  const when = (iso: string | null) =>
    iso ? formatDate(iso) : <span className="text-muted">{t('common.never')}</span>;
  return (
    <Card className="p-6">
      <h2 className="mb-4 text-lg font-semibold">{t('servers.overview')}</h2>
      <Facts
        items={[
          [t('servers.game'), `${server.templateName} (${details.templateVersion})`],
          ...details.ports
            .filter((port) => port.port !== null)
            .map((port): [string, ReactNode] => [
              port.label,
              <code key={port.name}>
                {port.ipAddress}:{port.port}
              </code>,
            ]),
          [
            t('servers.node'),
            <Link key="node" to={`/nodes/${server.nodeId}`} className="hover:text-accent hover:underline">
              {server.nodeName}
            </Link>,
          ],
          [
            t('servers.status'),
            <div key="status">
              <ServerStateBadge server={server} />
              <StateReason server={server} />
            </div>,
          ],
          [t('servers.desired'), t(`servers.desiredStates.${server.desiredState}`)],
          [t('servers.lastExitCode'), server.lastExitCode ?? '—'],
          [t('servers.lastStarted'), when(server.lastStartedAt)],
          [t('servers.lastStopped'), when(server.lastStoppedAt)],
          [t('servers.restartOnCrash'), details.restartOnCrash ? t('servers.yes') : t('servers.no')],
          [t('servers.shortId'), <code key="short">{server.shortId}</code>],
        ]}
      />
    </Card>
  );
}

const statusTones = {
  queued: 'neutral',
  running: 'accent',
  succeeded: 'success',
  failed: 'danger',
  cancelled: 'neutral',
  interrupted: 'danger',
} as const;

/** Who is playing, as the game answered its query: names are the players' own, shown as text. */
function PlayersCard({ query }: { query: ServerQuery }) {
  const { t } = useTranslation();
  const formatDuration = useFormatDuration();
  return (
    <Card className="p-6">
      <h2 className="text-lg font-semibold">{t('servers.players.title')}</h2>
      {(query.name || query.version) && (
        <p className="mt-1 text-sm break-words text-muted">
          {[query.name, query.version && t('servers.players.version', { version: query.version })]
            .filter(Boolean)
            .join(' · ')}
        </p>
      )}
      <div className="mt-4">
        {query.playerList.length === 0 ? (
          <p className="text-sm text-muted">
            {query.players > 0 ? t('servers.players.notListed') : t('servers.players.empty')}
          </p>
        ) : (
          <Table label={t('servers.players.title')}>
            <thead>
              <tr>
                <Th>{t('servers.players.name')}</Th>
                <Th>{t('servers.players.score')}</Th>
                <Th>{t('servers.players.time')}</Th>
              </tr>
            </thead>
            <tbody>
              {query.playerList.map((player, index) => (
                <tr key={index}>
                  <Td className="break-all">{player.name}</Td>
                  <Td className="tabular-nums">{player.score ?? '—'}</Td>
                  <Td className="tabular-nums">
                    {player.connectedSeconds === null ? '—' : formatDuration(player.connectedSeconds)}
                  </Td>
                </tr>
              ))}
            </tbody>
          </Table>
        )}
      </div>
    </Card>
  );
}

function OperationsCard({ operations }: { operations: OperationView[] }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  return (
    <Card className="p-6">
      <h2 className="mb-4 text-lg font-semibold">{t('servers.operations.title')}</h2>
      {operations.length === 0 ? (
        <p className="text-sm text-muted">{t('servers.operations.empty')}</p>
      ) : (
        <Table label={t('servers.operations.title')}>
          <thead>
            <tr>
              <Th>{t('servers.operations.type')}</Th>
              <Th>{t('servers.operations.status')}</Th>
              <Th>{t('servers.operations.when')}</Th>
            </tr>
          </thead>
          <tbody>
            {operations.map((operation) => (
              <tr key={operation.id}>
                <Td>{t(`servers.operations.types.${operation.type}`)}</Td>
                <Td>
                  <Badge tone={statusTones[operation.status]}>
                    {t(`servers.operations.statuses.${operation.status}`)}
                  </Badge>
                  {operation.errorCode && (operation.status === 'failed' || operation.status === 'interrupted') && (
                    <p className="mt-1 text-xs text-danger" title={operation.errorCode}>
                      {failureReason(t, operation.errorCode, operation.errorMessage)}
                    </p>
                  )}
                </Td>
                <Td>{formatDate(operation.completedAt ?? operation.createdAt)}</Td>
              </tr>
            ))}
          </tbody>
        </Table>
      )}
    </Card>
  );
}

function AdministrationSection({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const navigate = useNavigate();
  const reinstall = useReinstallServer();
  const remove = useDeleteServer();
  const update = useUpdateGame();
  const upgrade = useUpgradeTemplate();
  const [validate, setValidate] = useState(false);
  const { server } = details;
  const available = serverActions(server);
  const canInstall = canOnServer(me, details.server, Permission.ServerInstall);
  const canDelete = canOnServer(me, details.server, Permission.ServerDelete);
  if (!canInstall && !canDelete) {
    return null;
  }

  const error = reinstall.error ?? remove.error ?? update.error ?? upgrade.error;
  const next = details.templateUpgrade;
  return (
    <section className="mt-8 space-y-4">
      <h2 className="text-lg font-semibold">{t('servers.administration')}</h2>
      {error && <Alert>{errorMessage(t, error)}</Alert>}
      {canInstall && next && (
        <div className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-border p-3">
          <p className="text-sm">
            {t('servers.templateUpgrade', { current: details.templateVersion, next: next.version })}
          </p>
          <Button
            variant="secondary"
            disabled={!available.reinstall}
            busy={upgrade.isPending}
            onClick={() =>
              window.confirm(t('servers.templateUpgradeConfirm', { name: server.name, version: next.version })) &&
              upgrade.mutate({ id: server.id, templateId: next.templateId })
            }
          >
            {t('servers.templateUpgradeAction', { version: next.version })}
          </Button>
        </div>
      )}
      <div className="flex flex-wrap gap-2">
        {canInstall && details.canUpdate && (
          <Button
            variant="secondary"
            disabled={!available.reinstall || server.installState !== 'installed'}
            busy={update.isPending}
            onClick={() => update.mutate({ id: server.id, validate })}
          >
            {t('servers.updateGame')}
          </Button>
        )}
        {canInstall && (
          <Button
            variant="secondary"
            disabled={!available.reinstall}
            busy={reinstall.isPending}
            onClick={() =>
              window.confirm(t('servers.reinstallConfirm', { name: server.name })) && reinstall.mutate(server.id)
            }
          >
            {t('servers.reinstall')}
          </Button>
        )}
        {canDelete && (
          <Button
            variant="danger"
            disabled={!available.delete}
            busy={remove.isPending}
            onClick={() =>
              window.confirm(t('servers.deleteConfirm', { name: server.name })) &&
              remove.mutate(server.id, { onSuccess: () => void navigate('/servers') })
            }
          >
            {t('actions.delete')}
          </Button>
        )}
      </div>
      {canInstall && details.canUpdate && (
        <Checkbox
          label={t('servers.validateFiles')}
          checked={validate}
          onChange={(e) => setValidate(e.target.checked)}
        />
      )}
      <p className="text-xs text-muted">{t('servers.administrationHint')}</p>
    </section>
  );
}
