import { Cpu, HardDrive, MemoryStick, Server, type LucideIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Meter } from '../../components/ui/Display';
import { useFormatDate, useFormatGigabytes } from '../../lib/format';
import type { NodeSummary } from './api';
import { NodeStateBadge } from './components';
import { nodeState } from './nodeStatus';

/**
 * One node in the list: its state, the machine, and while its agent reports, how full its CPU, memory and the disk
 * with the game servers are. The whole card opens the node.
 */
export function NodeCard({ node }: { node: NodeSummary }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const formatGigabytes = useFormatGigabytes();
  const online = nodeState(node) === 'online';
  const metrics = online ? node.metrics : null;
  const disk = metrics?.disks?.find((d) => d.uses.includes('servers')) ?? metrics?.disks?.[0];
  const { inventory } = node;
  const used = (part: number, total: number) => (total > 0 ? (100 * part) / total : 0);

  return (
    <article
      aria-labelledby={`node-card-${node.id}`}
      className="group relative flex w-full min-w-0 flex-col gap-4 rounded-2xl border border-border bg-surface/90 p-5 shadow-lg shadow-black/20 transition hover:-translate-y-0.5 hover:border-accent/50 hover:shadow-accent/10 focus-within:border-accent/60 motion-reduce:transition-none motion-reduce:hover:translate-y-0"
    >
      <div className="flex items-start gap-3">
        <span
          aria-hidden="true"
          className={`grid size-11 shrink-0 place-items-center rounded-xl ring-1 ${
            online ? 'bg-success/15 text-success ring-success/25' : 'bg-surface-raised text-muted ring-border'
          }`}
        >
          <Server className="size-5" />
        </span>
        <div className="min-w-0 flex-1">
          <h2 id={`node-card-${node.id}`} className="truncate font-semibold">
            <Link
              to={`/nodes/${node.id}`}
              className="after:absolute after:inset-0 after:rounded-2xl hover:text-accent focus-visible:outline-none"
            >
              {node.name}
            </Link>
          </h2>
          <p className="truncate text-xs text-muted">
            {inventory ? `${inventory.hostname} · ${inventory.osDescription}` : t('nodes.card.noMachine')}
          </p>
        </div>
        <NodeStateBadge node={node} />
      </div>
      {node.description && <p className="line-clamp-2 text-sm text-muted">{node.description}</p>}
      {metrics ? (
        <dl aria-label={t('nodes.usage')} className="space-y-3 text-sm">
          <Usage
            icon={Cpu}
            label={t('nodes.card.cpu')}
            value={t('nodes.card.percent', { value: metrics.cpuPercent })}
            percent={metrics.cpuPercent}
          />
          <Usage
            icon={MemoryStick}
            label={t('nodes.card.memory')}
            value={t('nodes.memoryValue', {
              used: formatGigabytes(metrics.memoryUsedBytes),
              total: formatGigabytes(metrics.memoryTotalBytes),
            })}
            percent={used(metrics.memoryUsedBytes, metrics.memoryTotalBytes)}
          />
          {disk && (
            <Usage
              icon={HardDrive}
              label={t('nodes.card.disk', { root: disk.root })}
              value={t('nodes.memoryValue', {
                used: formatGigabytes(disk.totalBytes - disk.freeBytes),
                total: formatGigabytes(disk.totalBytes),
              })}
              percent={used(disk.totalBytes - disk.freeBytes, disk.totalBytes)}
            />
          )}
        </dl>
      ) : (
        // Just connected, its agent has not reported yet (the first report comes within seconds).
        <p className="text-sm text-muted">{online ? t('nodes.card.waiting') : t('nodes.card.noUsage')}</p>
      )}
      <div className="mt-auto flex flex-wrap items-center justify-between gap-x-3 gap-y-1 border-t border-border pt-3 text-xs text-muted">
        <span className="min-w-0 truncate">
          {inventory &&
            [t('nodes.card.cores', { count: inventory.logicalCores }), inventory.cpuModel].filter(Boolean).join(' · ')}
        </span>
        <span>
          {node.lastSeenAt
            ? t('nodes.card.lastSeen', { when: formatDate(node.lastSeenAt) })
            : t('nodes.card.neverSeen')}
        </span>
      </div>
    </article>
  );
}

function Usage({
  icon: Icon,
  label,
  value,
  percent,
}: {
  icon: LucideIcon;
  label: string;
  value: string;
  percent: number;
}) {
  return (
    <div className="grid grid-cols-[1fr_auto] items-center gap-x-2 gap-y-1.5">
      <dt className="flex min-w-0 items-center gap-1.5 text-muted">
        <Icon aria-hidden="true" className="size-4 shrink-0" />
        <span className="truncate">{label}</span>
      </dt>
      <dd className="text-right font-medium tabular-nums">{value}</dd>
      <dd className="col-span-2">
        <Meter percent={percent} />
      </dd>
    </div>
  );
}
