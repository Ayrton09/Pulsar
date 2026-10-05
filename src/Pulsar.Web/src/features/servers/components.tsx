import { useTranslation } from 'react-i18next';
import { Badge } from '../../components/ui/Display';
import type { ServerSummary } from './api';
import { serverDisplayState, type ServerDisplayState } from './serverStatus';

const tones: Record<ServerDisplayState, 'neutral' | 'accent' | 'success' | 'danger'> = {
  installing: 'accent',
  restoring: 'accent',
  deleting: 'accent',
  installFailed: 'danger',
  notInstalled: 'neutral',
  starting: 'accent',
  online: 'success',
  stopping: 'accent',
  offline: 'neutral',
  crashed: 'danger',
};

/**
 * The server's status. While its node is disconnected the panel only knows the last state it heard, so that one is
 * shown as such, without the color of a live state.
 */
export function ServerStateBadge({
  server,
}: {
  server: Pick<ServerSummary, 'installState' | 'runtimeState' | 'activeOperation' | 'nodeConnected'>;
}) {
  const { t } = useTranslation();
  const state = t(`servers.states.${serverDisplayState(server)}`);
  return server.nodeConnected ? (
    <Badge tone={tones[serverDisplayState(server)]}>{state}</Badge>
  ) : (
    <Badge>{t('servers.lastKnown', { state })}</Badge>
  );
}

/** Shown when the panel cannot reach the node: the state above is the last one it heard. */
export function NodeDisconnectedBadge() {
  const { t } = useTranslation();
  return <Badge tone="danger">{t('servers.nodeDisconnected')}</Badge>;
}

/** Why the server is in its state, when that needs explaining (a crash, a refused start, a timeout). */
export function StateReason({
  server,
}: {
  server: Pick<ServerSummary, 'lastStateReason' | 'lastExitCode' | 'runtimeState'>;
}) {
  const { t } = useTranslation();
  const reason = server.lastStateReason;
  if (!reason || !explained.has(reason)) {
    return null;
  }

  return <p className="text-xs text-muted">{t(`servers.reasons.${reason}`, { code: server.lastExitCode ?? '—' })}</p>;
}

const explained = new Set<string>([
  'readinessTimedOut',
  'exitedByItself',
  'crashed',
  'crashLoop',
  'stopTimedOut',
  'killed',
  'startFailed',
  'agentStopping',
  'agentStarted',
  'autoRestart',
  'memoryLimit',
]);
