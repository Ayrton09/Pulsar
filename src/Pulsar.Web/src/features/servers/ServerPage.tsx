import {
  Archive,
  CalendarClock,
  Clock,
  Cpu,
  Database,
  Download,
  FolderOpen,
  LayoutDashboard,
  Map as MapIcon,
  MemoryStick,
  Network,
  OctagonX,
  Play,
  Puzzle,
  RotateCcw,
  RotateCw,
  Settings,
  Square,
  Terminal,
  Trash2,
  Users,
  type LucideIcon,
} from 'lucide-react';
import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
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

const tabIcons: Record<Tab, LucideIcon> = {
  overview: LayoutDashboard,
  console: Terminal,
  files: FolderOpen,
  addons: Puzzle,
  backups: Archive,
  databases: Database,
  schedules: CalendarClock,
  settings: Settings,
  network: Network,
};

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
        status={
          <>
            <ServerStateBadge server={server} />
            {!server.nodeConnected && <NodeDisconnectedBadge />}
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
      <SectionTabs tabs={tabs} current={tab} onOpen={openTab} />
      <div role="tabpanel" id="server-section" aria-labelledby={`server-tab-${tab}`} className="mt-6">
        {tab === 'overview' && (
          <>
            <div className="grid grid-cols-1 gap-6 xl:grid-cols-2">
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

/**
 * The server's sections as tabs. On a narrow screen they scroll sideways, keeping the open one in view. Only the open
 * one is in the Tab order; the arrow keys, Home and End move between them (the WAI-ARIA tabs pattern).
 */
function SectionTabs({ tabs, current, onOpen }: { tabs: Tab[]; current: Tab; onOpen: (tab: Tab) => void }) {
  const { t } = useTranslation();
  const list = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const strip = list.current;
    const open = strip?.querySelector<HTMLElement>('[aria-selected="true"]');
    if (!strip || !open) {
      return;
    }
    // Scrolls the strip only, never the page (scrollIntoView would also bring the strip itself into view).
    const margin = 24;
    if (open.offsetLeft < strip.scrollLeft) {
      strip.scrollLeft = open.offsetLeft - margin;
    } else if (open.offsetLeft + open.offsetWidth > strip.scrollLeft + strip.clientWidth) {
      strip.scrollLeft = open.offsetLeft + open.offsetWidth - strip.clientWidth + margin;
    }
  }, [current]);

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    // With a modifier the keys are the browser's (Alt+Left goes back, Ctrl+End scrolls the page).
    if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) {
      return;
    }
    // From the tab that has focus, which is not always the open one: leaving a Files editor with unsaved changes
    // can be refused after focus moved on.
    const focused = event.target instanceof HTMLElement ? event.target.id : '';
    const at = tabs.findIndex((name) => `server-tab-${name}` === focused);
    const index = at === -1 ? tabs.indexOf(current) : at;
    const target =
      event.key === 'ArrowRight'
        ? tabs[(index + 1) % tabs.length]
        : event.key === 'ArrowLeft'
          ? tabs[(index - 1 + tabs.length) % tabs.length]
          : event.key === 'Home'
            ? tabs[0]
            : event.key === 'End'
              ? tabs[tabs.length - 1]
              : undefined;
    if (target === undefined) {
      return;
    }
    event.preventDefault();
    onOpen(target);
    document.getElementById(`server-tab-${target}`)?.focus();
  };

  return (
    <div
      ref={list}
      role="tablist"
      aria-label={t('servers.tabs.label')}
      onKeyDown={onKeyDown}
      className="relative mt-6 flex gap-1 overflow-x-auto shadow-[inset_0_-1px_0_var(--color-border)] [scrollbar-width:thin]"
    >
      {tabs.map((name) => {
        const Icon = tabIcons[name];
        const selected = current === name;
        return (
          <button
            key={name}
            id={`server-tab-${name}`}
            type="button"
            role="tab"
            aria-selected={selected}
            aria-controls="server-section"
            tabIndex={selected ? 0 : -1}
            onClick={() => onOpen(name)}
            className={`flex shrink-0 items-center gap-2 border-b-2 px-3 py-2.5 text-sm whitespace-nowrap transition-colors focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-accent sm:px-4 ${selected ? 'border-accent font-medium text-accent' : 'border-transparent text-muted hover:text-text'}`}
          >
            <Icon className="size-4" />
            {t(`servers.tabs.${name}`)}
          </button>
        );
      })}
    </div>
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
  const items: [LucideIcon, string, string][] = [
    [
      Cpu,
      t('servers.usage.cpu'),
      limits.cpuPercent == null
        ? cpu
        : t('servers.usage.ofLimit', { value: cpu, limit: t('servers.usage.cpuLimit', { value: limits.cpuPercent }) }),
    ],
    [
      MemoryStick,
      t('servers.usage.memory'),
      memoryLimit == null
        ? formatBytes(metrics.memoryBytes)
        : t('servers.usage.ofLimit', {
            value: formatBytes(metrics.committedBytes ?? metrics.memoryBytes),
            limit: formatBytes(memoryLimit),
          }),
    ],
    [Clock, t('servers.usage.uptime'), formatDuration(metrics.uptimeSeconds)],
  ];
  if (query) {
    const players = t('servers.usage.playersValue', { players: query.players, max: query.maxPlayers });
    items.push([
      Users,
      t('servers.usage.players'),
      query.bots > 0 ? `${players} ${t('servers.usage.bots', { count: query.bots })}` : players,
    ]);
    if (query.map) {
      items.push([MapIcon, t('servers.usage.map'), query.map]);
    }
  }
  return (
    <dl aria-label={t('servers.usage.label')} className="mb-4 grid grid-cols-2 gap-2 text-sm sm:flex sm:flex-wrap">
      {items.map(([Icon, label, value]) => (
        <div key={label} className="min-w-0 rounded-lg border border-border bg-surface px-3 py-2 sm:min-w-32">
          <dt className="flex items-center gap-1.5 text-xs text-muted">
            <Icon className="size-3.5 shrink-0" />
            {label}
          </dt>
          <dd className="mt-0.5 truncate font-medium tabular-nums" title={value}>
            {value}
          </dd>
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
  const buttons: [ServerCommand, string, boolean, LucideIcon][] = [
    ['start', Permission.ServerStart, available.start, Play],
    ['stop', Permission.ServerStop, available.stop, Square],
    ['restart', Permission.ServerRestart, available.restart, RotateCw],
    ['kill', Permission.ServerKill, available.kill, OctagonX],
  ];
  const visible = buttons.filter(([, permission]) => canOnServer(me, server, permission));
  if (visible.length === 0) {
    return null;
  }

  const refused = command.data && command.data.status !== 'accepted' && command.data.status !== 'alreadyInState';
  return (
    <section aria-label={t('servers.actions')} className="space-y-2">
      <div className="flex flex-wrap gap-2">
        {visible.map(([name, , enabled, icon]) => (
          <Button
            key={name}
            icon={icon}
            // Killing loses unsaved progress: it looks dangerous, without outshouting the everyday actions.
            variant={name === 'kill' ? 'dangerSubtle' : name === 'start' ? 'primary' : 'secondary'}
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
                <Td className="whitespace-nowrap">{formatDate(operation.completedAt ?? operation.createdAt)}</Td>
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
            icon={Download}
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
            icon={RotateCcw}
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
            icon={Trash2}
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
