import { CircleCheck, Cpu, HardDrive, MemoryStick, Server, TriangleAlert, type LucideIcon } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Card, Meter, PageHeader } from '../../components/ui/Display';
import { useFormatDate, useFormatGigabytes } from '../../lib/format';
import { can, canAnywhere, Permission } from '../../lib/permissions';
import { useRecentActivity, type AuditEntry } from '../admin/api';
import { useCurrentUser } from '../auth/api';
import { useWatchNodes, useWatchServers } from '../live/hooks';
import { useNodes, type NodeSummary } from '../nodes/api';
import { nodeState } from '../nodes/nodeStatus';
import { useServers, type ServerSummary } from '../servers/api';
import { ServerCard } from '../servers/ServerCard';

/**
 * Dashboard v1 (docs/architecture/07): servers and nodes at a glance, the nodes' combined usage, what needs attention
 * and the latest activity. Each part shows only with the permission it needs, and stays current through the live
 * channel.
 */
export function DashboardPage() {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const canServers = canAnywhere(me, Permission.ServerView);
  const canNodes = canAnywhere(me, Permission.NodeView, Permission.NodeManage);
  const canAudit = can(me, Permission.AuditView);
  const servers = useServersIf(canServers);
  const nodes = useNodesIf(canNodes);
  const activity = useRecentActivity(8, canAudit);

  return (
    <>
      <PageHeader
        title={t('dashboard.title')}
        description={t('dashboard.welcome', { name: me.displayName ?? me.userName })}
      />
      {!canServers && !canNodes && !canAudit && (
        <Card className="p-6">
          <p className="text-sm text-muted">{t('dashboard.nothing')}</p>
        </Card>
      )}
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        {servers && <ServersCard servers={servers} />}
        {nodes && <NodesCard nodes={nodes} />}
        {nodes && <UsageCards nodes={nodes} />}
      </div>
      {(servers || nodes) && <Alerts servers={servers ?? []} nodes={nodes ?? []} />}
      {servers && servers.length > 0 && <ServerCards servers={servers} />}
      {canAudit && activity.data && <RecentActivity entries={activity.data.entries} />}
    </>
  );
}

function useServersIf(enabled: boolean): ServerSummary[] | null {
  // Hooks run either way; without the permission nothing is fetched or watched.
  const servers = useServers(enabled);
  useWatchServers(enabled);
  return enabled ? (servers.data ?? null) : null;
}

function useNodesIf(enabled: boolean): NodeSummary[] | null {
  const nodes = useNodes(enabled);
  useWatchNodes(enabled);
  return enabled ? (nodes.data ?? null) : null;
}

function Stat({
  title,
  icon: Icon,
  value,
  detail,
  usage,
  to,
}: {
  title: string;
  icon: LucideIcon;
  value: ReactNode;
  detail?: ReactNode;
  /** A share in use (0–100), drawn as a bar under the value. */
  usage?: number | null;
  to?: string;
}) {
  const body = (
    <Card className="h-full p-5">
      <div className="flex items-start justify-between gap-3">
        <p className="text-sm text-muted">{title}</p>
        <span className="grid size-9 shrink-0 place-items-center rounded-xl bg-accent/15 text-accent ring-1 ring-accent/20">
          <Icon className="size-4.5" />
        </span>
      </div>
      <p className="mt-1 text-2xl font-semibold tabular-nums">{value}</p>
      {usage != null && <Meter percent={usage} className="mt-3" />}
      {detail && <p className="mt-2 text-xs text-muted">{detail}</p>}
    </Card>
  );
  return to ? (
    <Link to={to} className="block rounded-2xl transition hover:ring-1 hover:ring-accent/60">
      {body}
    </Link>
  ) : (
    body
  );
}

function ServersCard({ servers }: { servers: ServerSummary[] }) {
  const { t } = useTranslation();
  const running = servers.filter((s) => s.runtimeState === 'running' || s.runtimeState === 'starting').length;
  return (
    <Stat
      title={t('dashboard.servers')}
      icon={Server}
      value={t('dashboard.runningOf', { running, total: servers.length })}
      detail={t('dashboard.serversOffline', { count: servers.length - running })}
      to="/servers"
    />
  );
}

function NodesCard({ nodes }: { nodes: NodeSummary[] }) {
  const { t } = useTranslation();
  // Nodes not enrolled yet, waiting for approval or disabled are not expected online (and are not alerts either).
  const enrolled = nodes.filter((n) => ['online', 'offline'].includes(nodeState(n)));
  const online = enrolled.filter((n) => nodeState(n) === 'online').length;
  const disabled = nodes.filter((n) => nodeState(n) === 'disabled').length;
  const offline = t('dashboard.nodesOffline', { count: enrolled.length - online });
  return (
    <Stat
      title={t('dashboard.nodes')}
      icon={HardDrive}
      value={t('dashboard.onlineOf', { online, total: enrolled.length })}
      detail={disabled > 0 ? `${offline} · ${t('dashboard.nodesDisabled', { count: disabled })}` : offline}
      to="/nodes"
    />
  );
}

/** CPU weighted by each node's cores, memory added up: the combined load of every connected node. */
function UsageCards({ nodes }: { nodes: NodeSummary[] }) {
  const { t } = useTranslation();
  const formatGigabytes = useFormatGigabytes();
  const reporting = nodes.flatMap((n) =>
    nodeState(n) === 'online' && n.metrics ? [{ metrics: n.metrics, cores: n.inventory?.logicalCores ?? 1 }] : [],
  );
  const cores = reporting.reduce((sum, n) => sum + n.cores, 0);
  const cpu = cores === 0 ? null : reporting.reduce((sum, n) => sum + n.metrics.cpuPercent * n.cores, 0) / cores;
  const used = reporting.reduce((sum, n) => sum + n.metrics.memoryUsedBytes, 0);
  const total = reporting.reduce((sum, n) => sum + n.metrics.memoryTotalBytes, 0);
  const memory = total === 0 ? null : Math.round((100 * used) / total);
  return (
    <>
      <Stat
        title={t('dashboard.cpu')}
        icon={Cpu}
        value={cpu === null ? '—' : t('nodes.cpuValue', { value: Math.round(cpu * 10) / 10 })}
        usage={cpu}
        detail={t('dashboard.acrossNodes', { count: reporting.length })}
      />
      <Stat
        title={t('dashboard.memory')}
        icon={MemoryStick}
        value={memory === null ? '—' : `${memory} %`}
        usage={memory}
        detail={
          total === 0
            ? undefined
            : t('dashboard.memoryDetail', { used: formatGigabytes(used), total: formatGigabytes(total) })
        }
      />
    </>
  );
}

function Alerts({ servers, nodes }: { servers: ServerSummary[]; nodes: NodeSummary[] }) {
  const { t } = useTranslation();
  const items: { key: string; to: string; text: string }[] = [
    ...servers
      .filter((s) => s.runtimeState === 'crashed')
      .map((s) => ({
        key: `server-${s.id}`,
        to: `/servers/${s.id}`,
        text: t(s.lastStateReason === 'crashLoop' ? 'dashboard.alerts.crashLoop' : 'dashboard.alerts.crashed', {
          name: s.name,
        }),
      })),
    ...nodes
      .filter((n) => nodeState(n) === 'offline')
      .map((n) => ({
        key: `node-${n.id}`,
        to: `/nodes/${n.id}`,
        text: t('dashboard.alerts.nodeOffline', { name: n.name }),
      })),
  ];
  // All clear is one quiet line; problems get the room and the color.
  return items.length === 0 ? (
    <Card className="mt-6 flex items-center gap-3 px-6 py-4">
      <CircleCheck className="size-5 shrink-0 text-success" />
      <div className="text-sm">
        <h2 className="font-semibold">{t('dashboard.alerts.title')}</h2>
        <p className="text-muted">{t('dashboard.alerts.none')}</p>
      </div>
    </Card>
  ) : (
    <Card className="mt-6 p-6">
      <h2 className="mb-3 flex items-center gap-2 text-lg font-semibold">
        <TriangleAlert className="size-5 text-danger" />
        {t('dashboard.alerts.title')}
      </h2>
      <ul className="space-y-2 text-sm">
        {items.map((item) => (
          <li key={item.key}>
            <Link to={item.to} className="text-danger hover:underline">
              {item.text}
            </Link>
          </li>
        ))}
      </ul>
    </Card>
  );
}

/** The servers as cards, the running ones first, up to six; the Servers page has them all. */
function ServerCards({ servers }: { servers: ServerSummary[] }) {
  const { t } = useTranslation();
  const alive = (server: ServerSummary) => server.runtimeState === 'running' || server.runtimeState === 'starting';
  const shown = [...servers].sort((a, b) => Number(alive(b)) - Number(alive(a))).slice(0, 6);
  return (
    <section aria-labelledby="dashboard-servers" className="mt-8">
      <div className="mb-4 flex items-baseline justify-between gap-3">
        <h2 id="dashboard-servers" className="text-lg font-semibold">
          {t('dashboard.yourServers')}
        </h2>
        <Link to="/servers" className="text-sm text-accent hover:underline">
          {t('dashboard.allServers', { count: servers.length })}
        </Link>
      </div>
      <ul className="grid grid-cols-1 gap-5 sm:grid-cols-2 xl:grid-cols-3">
        {shown.map((server) => (
          <li key={server.id} className="flex min-w-0">
            <ServerCard server={server} />
          </li>
        ))}
      </ul>
    </section>
  );
}

function RecentActivity({ entries }: { entries: AuditEntry[] }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  return (
    <Card className="mt-8 p-6">
      <div className="mb-3 flex items-baseline justify-between">
        <h2 className="text-lg font-semibold">{t('dashboard.activity')}</h2>
        <Link to="/admin/audit" className="text-sm text-accent hover:underline">
          {t('dashboard.allActivity')}
        </Link>
      </div>
      {entries.length === 0 ? (
        <p className="text-sm text-muted">{t('audit.empty')}</p>
      ) : (
        <ul className="divide-y divide-border text-sm">
          {entries.map((entry) => (
            <li key={entry.id} className="flex flex-wrap justify-between gap-2 py-2">
              <span>
                {t(`audit.actions.${entry.action}`, { defaultValue: entry.action })}
                <span className="text-muted">
                  {' '}
                  · {entry.actorType === 'user' ? entry.actorDisplay : t(`audit.actors.${entry.actorType}`)}
                </span>
              </span>
              <span className="text-muted">{formatDate(entry.occurredAt)}</span>
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}
