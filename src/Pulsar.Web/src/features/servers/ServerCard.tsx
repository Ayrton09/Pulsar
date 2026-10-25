import { Check, Copy, Cpu, MemoryStick, Users } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Spinner } from '../../components/ui/Button';
import { Meter } from '../../components/ui/Display';
import { copyText } from '../../lib/clipboard';
import { useFormatBytes, useFormatDate } from '../../lib/format';
import { useArtworkShown } from '../games/api';
import { GameArt } from '../games/GameArt';
import { useServerMetrics } from '../live/hooks';
import type { ServerSummary } from './api';
import { GameUpdateBadge, NodeDisconnectedBadge, ServerStatusPill, StateReason } from './components';
import { PowerButtons } from './PowerButtons';

/**
 * One server in the list: its game's picture with its status, its live usage while it runs, where players connect and
 * the power buttons. The whole card opens the server; the address and the buttons sit above that link.
 */
export function ServerCard({ server, headingLevel = 2 }: { server: ServerSummary; headingLevel?: 2 | 3 }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const alive = server.runtimeState === 'running' || server.runtimeState === 'starting';
  const Heading = headingLevel === 3 ? 'h3' : 'h2';
  return (
    <article
      aria-labelledby={`server-card-${server.id}`}
      className="group relative flex w-full min-w-0 flex-col overflow-hidden rounded-2xl border border-border bg-surface/90 shadow-lg shadow-black/20 transition hover:-translate-y-0.5 hover:border-accent/50 hover:shadow-accent/10 focus-within:border-accent/60 motion-reduce:transition-none motion-reduce:hover:translate-y-0"
    >
      <div className="relative aspect-[460/215] overflow-hidden">
        <GameArt
          templateKey={server.templateKey}
          kind="header"
          className="absolute inset-0 size-full transition-transform duration-500 group-hover:scale-105 motion-reduce:transition-none"
        />
        <div className="absolute inset-x-0 bottom-0 h-1/3 bg-linear-to-t from-surface to-transparent" />
        <div className="absolute top-3 left-3 flex flex-wrap gap-1.5">
          <ServerStatusPill server={server} />
          {!server.nodeConnected && (
            <span className="rounded-full bg-black/70 backdrop-blur-sm">
              <NodeDisconnectedBadge />
            </span>
          )}
          {server.gameUpdateAvailable && (
            <span className="rounded-full bg-black/70 backdrop-blur-sm">
              <GameUpdateBadge />
            </span>
          )}
        </div>
      </div>
      <div className="flex flex-1 flex-col gap-3 px-4 pb-4">
        <div className="min-w-0">
          <Heading id={`server-card-${server.id}`} className="truncate text-base font-semibold">
            <Link
              to={`/servers/${server.id}`}
              className="after:absolute after:inset-0 after:rounded-2xl hover:text-accent focus-visible:outline-none"
            >
              {server.name}
            </Link>
          </Heading>
          <p className="truncate text-xs text-muted">
            {server.templateName} · {server.nodeName}
          </p>
        </div>
        {alive && server.nodeConnected ? (
          <div className="space-y-2">
            <CardUsage serverId={server.id} />
            <StateReason server={server} />
          </div>
        ) : (
          <div className="text-xs text-muted">
            <p>
              {server.lastStartedAt
                ? t('servers.card.lastStarted', { when: formatDate(server.lastStartedAt) })
                : t('servers.card.neverStarted')}
            </p>
            <StateReason server={server} />
          </div>
        )}
        <div className="mt-auto flex min-h-9 items-center justify-between gap-2 border-t border-border pt-3">
          <AddressChip address={server.gameAddress} />
          <div className="relative z-10">
            <PowerButtons server={server} compact />
          </div>
        </div>
      </div>
    </article>
  );
}

/**
 * Servers as cards with their game's picture or, when the panel shows no game artwork, as a list without pictures
 * (rows with the same contents). Waits until the panel says which.
 */
export function ServerCollection({
  servers,
  label,
  headingLevel = 2,
  gridClassName,
}: {
  servers: ServerSummary[];
  label?: string | undefined;
  headingLevel?: 2 | 3;
  /** The cards' columns. */
  gridClassName: string;
}) {
  const { t } = useTranslation();
  const artwork = useArtworkShown();
  if (artwork === undefined) {
    return <Spinner label={t('common.loading')} />;
  }
  if (!artwork) {
    return (
      <ul
        aria-label={label}
        className="divide-y divide-border overflow-hidden rounded-2xl border border-border bg-surface/90 shadow-lg shadow-black/20"
      >
        {servers.map((server) => (
          <li key={server.id}>
            <ServerRow server={server} headingLevel={headingLevel} />
          </li>
        ))}
      </ul>
    );
  }
  return (
    <ul aria-label={label} className={`grid grid-cols-1 gap-5 ${gridClassName}`}>
      {servers.map((server) => (
        <li key={server.id} className="flex min-w-0">
          <ServerCard server={server} headingLevel={headingLevel} />
        </li>
      ))}
    </ul>
  );
}

/** One server as a row of the list shown without game artwork: what its card says, without the picture. */
export function ServerRow({ server, headingLevel = 2 }: { server: ServerSummary; headingLevel?: 2 | 3 }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const alive = server.runtimeState === 'running' || server.runtimeState === 'starting';
  const Heading = headingLevel === 3 ? 'h3' : 'h2';
  return (
    <article
      aria-labelledby={`server-card-${server.id}`}
      className="relative flex flex-col gap-3 px-4 py-3 transition-colors focus-within:bg-surface-raised/40 hover:bg-surface-raised/40 lg:flex-row lg:items-center lg:gap-6"
    >
      <div className="min-w-0 flex-1">
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          <Heading id={`server-card-${server.id}`} className="min-w-0 truncate text-base font-semibold">
            <Link
              to={`/servers/${server.id}`}
              className="after:absolute after:inset-0 hover:text-accent focus-visible:outline-none"
            >
              {server.name}
            </Link>
          </Heading>
          <ServerStatusPill server={server} />
          {!server.nodeConnected && <NodeDisconnectedBadge />}
          {server.gameUpdateAvailable && <GameUpdateBadge />}
        </div>
        <p className="truncate text-xs text-muted">
          {server.templateName} · {server.nodeName}
        </p>
        <StateReason server={server} />
      </div>
      <div className="lg:w-72 lg:shrink-0">
        {alive && server.nodeConnected ? (
          <CardUsage serverId={server.id} />
        ) : (
          <p className="text-xs text-muted">
            {server.lastStartedAt
              ? t('servers.card.lastStarted', { when: formatDate(server.lastStartedAt) })
              : t('servers.card.neverStarted')}
          </p>
        )}
      </div>
      <div className="flex min-h-9 items-center justify-between gap-2 lg:shrink-0 lg:justify-end">
        <AddressChip address={server.gameAddress} />
        <div className="relative z-10">
          <PowerButtons server={server} compact />
        </div>
      </div>
    </article>
  );
}

/** CPU, memory and players of a running server, pushed every few seconds. */
function CardUsage({ serverId }: { serverId: string }) {
  const { t } = useTranslation();
  const formatBytes = useFormatBytes();
  const metrics = useServerMetrics(serverId);
  const query = metrics?.query;
  return (
    <dl aria-label={t('servers.usage.label')} className="grid grid-cols-3 gap-3 text-xs">
      <div className="min-w-0">
        <dt className="flex items-center gap-1 text-muted">
          <Cpu aria-hidden="true" className="size-3.5 shrink-0" />
          {t('servers.usage.cpu')}
        </dt>
        <dd className="mt-0.5 truncate font-medium tabular-nums">
          {metrics ? t('servers.usage.cpuValue', { value: metrics.cpuPercent }) : '—'}
        </dd>
        <dd className="mt-1.5">
          <Meter percent={metrics?.cpuPercent ?? 0} />
        </dd>
      </div>
      <div className="min-w-0">
        <dt className="flex items-center gap-1 text-muted">
          <MemoryStick aria-hidden="true" className="size-3.5 shrink-0" />
          {t('servers.usage.memory')}
        </dt>
        <dd className="mt-0.5 truncate font-medium tabular-nums">{metrics ? formatBytes(metrics.memoryBytes) : '—'}</dd>
      </div>
      <div className="min-w-0">
        <dt className="flex items-center gap-1 text-muted">
          <Users aria-hidden="true" className="size-3.5 shrink-0" />
          {t('servers.usage.players')}
        </dt>
        <dd className="mt-0.5 truncate font-medium tabular-nums">
          {query ? t('servers.usage.playersValue', { players: query.players, max: query.maxPlayers }) : '—'}
        </dd>
        {query && query.maxPlayers > 0 && (
          <dd className="mt-1.5">
            <Meter percent={(query.players / query.maxPlayers) * 100} />
          </dd>
        )}
      </div>
    </dl>
  );
}

/**
 * Where players connect, copied with a click. A server listening on every address shows its port only: the address
 * players use depends on their network (the node's LAN address at home, the public one from outside).
 */
export function AddressChip({ address }: { address: ServerSummary['gameAddress'] }) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState<boolean | null>(null);
  if (!address) {
    return <span />;
  }

  const everywhere = address.ipAddress === '0.0.0.0';
  const text = everywhere ? String(address.port) : `${address.ipAddress}:${address.port}`;
  const shown = everywhere ? t('servers.card.port', { port: address.port }) : text;
  return (
    <span className="relative z-10 inline-flex min-w-0 items-center gap-2">
      <button
        type="button"
        onClick={() => void copyText(text).then(setCopied)}
        aria-label={t('servers.card.copyAddress', { address: shown })}
        title={t('servers.card.copyAddress', { address: shown })}
        className="inline-flex min-w-0 items-center gap-1.5 rounded-md bg-surface-raised px-2 py-1 font-mono text-xs text-muted transition-colors hover:text-text focus-visible:outline-2 focus-visible:outline-accent"
      >
        {copied ? (
          <Check aria-hidden="true" className="size-3.5 shrink-0 text-success" />
        ) : (
          <Copy aria-hidden="true" className="size-3.5 shrink-0" />
        )}
        <span className="truncate">{shown}</span>
      </button>
      {copied === true && (
        <span role="status" className="sr-only">
          {t('servers.card.copied')}
        </span>
      )}
      {copied === false && (
        <span role="status" className="text-xs text-danger">
          {t('servers.card.copyFailed')}
        </span>
      )}
    </span>
  );
}
