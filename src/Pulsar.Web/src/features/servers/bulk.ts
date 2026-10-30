import type { TFunction } from 'i18next';
import { api, unwrap } from '../../api/client';
import { errorMessage } from '../../lib/errors';
import { canOnServer, Permission } from '../../lib/permissions';
import type { CurrentUser } from '../auth/api';
import type { ServerSummary } from './api';
import { commandRefusal, serverActions } from './serverStatus';

/** What can be done to several servers at once (gap point 14): the everyday actions, never kill nor delete. */
export const bulkActions = ['start', 'restart', 'stop', 'backup', 'update'] as const;
export type BulkAction = (typeof bulkActions)[number];

export type BulkResult = 'done' | 'skipped' | 'failed';

export interface BulkOutcome {
  id: string;
  name: string;
  result: BulkResult;
  /** Why it was skipped or failed. */
  message?: string;
}

const permissions: Record<BulkAction, string> = {
  start: Permission.ServerStart,
  restart: Permission.ServerRestart,
  stop: Permission.ServerStop,
  backup: Permission.ServerBackupsCreate,
  update: Permission.ServerInstall,
};

/** The servers at once a run sends requests for: a few, so a long list does not flood the panel or its nodes. */
const parallel = 4;

/**
 * Why the action is not asked for this server (a reason's key under servers.bulk.skip), or null when it is: the user's
 * permission on it, then whether it makes sense now. The panel checks both again on each request.
 */
export function skipReason(action: BulkAction, server: ServerSummary, me: CurrentUser): string | null {
  if (!canOnServer(me, server, permissions[action])) {
    return 'forbidden';
  }
  const available = serverActions(server);
  const installed = server.installState === 'installed';
  switch (action) {
    case 'start':
      return available.start ? null : 'notStopped';
    case 'restart':
    case 'stop':
      return available[action] ? null : 'notRunning';
    case 'backup':
      return server.nodeConnected && installed && server.activeOperation === null ? null : 'busy';
    case 'update':
      return available.reinstall && installed ? null : 'mustBeStopped';
  }
}

/** Asks the panel for the action on one server; the reason when it refused. */
async function request(t: TFunction, action: BulkAction, id: string): Promise<string | null> {
  const path = { params: { path: { id } } };
  switch (action) {
    case 'start':
    case 'restart':
    case 'stop': {
      const ack = await unwrap(
        action === 'start'
          ? api.POST('/api/v1/servers/{id}/start', path)
          : action === 'stop'
            ? api.POST('/api/v1/servers/{id}/stop', path)
            : api.POST('/api/v1/servers/{id}/restart', path),
      );
      return ack.status === 'accepted' || ack.status === 'alreadyInState'
        ? null
        : commandRefusal(t, ack.status, ack.errorCode);
    }
    case 'backup':
      await unwrap(api.POST('/api/v1/servers/{id}/backups', { ...path, body: { name: null } }));
      return null;
    case 'update':
      await unwrap(api.POST('/api/v1/servers/{id}/update', { ...path, body: { validate: false } }));
      return null;
  }
}

/**
 * Runs the action on every server it applies to, a few at a time, each as its own request (its permission and its
 * audit entry are the server's, as from its page). The others are skipped with their reason. Reports each outcome as
 * it comes; resolves with all of them, in the servers' order.
 */
export async function runBulk(
  t: TFunction,
  action: BulkAction,
  servers: ServerSummary[],
  me: CurrentUser,
  onOutcome: (outcome: BulkOutcome) => void,
): Promise<BulkOutcome[]> {
  const outcomes = new Map<string, BulkOutcome>();
  const report = (outcome: BulkOutcome) => {
    outcomes.set(outcome.id, outcome);
    onOutcome(outcome);
  };
  const queue: ServerSummary[] = [];
  for (const server of servers) {
    const reason = skipReason(action, server, me);
    if (reason === null) {
      queue.push(server);
    } else {
      report({ id: server.id, name: server.name, result: 'skipped', message: t(`servers.bulk.skip.${reason}`) });
    }
  }

  const worker = async () => {
    for (let server = queue.shift(); server; server = queue.shift()) {
      try {
        const refused = await request(t, action, server.id);
        report(
          refused === null
            ? { id: server.id, name: server.name, result: 'done' }
            : { id: server.id, name: server.name, result: 'failed', message: refused },
        );
      } catch (error) {
        report({ id: server.id, name: server.name, result: 'failed', message: errorMessage(t, error) });
      }
    }
  };
  await Promise.all(Array.from({ length: Math.min(parallel, queue.length) }, worker));
  return servers.map((server) => outcomes.get(server.id)).filter((outcome) => outcome !== undefined);
}
