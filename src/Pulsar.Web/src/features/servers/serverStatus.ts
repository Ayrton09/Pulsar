import type { TFunction } from 'i18next';
import type { Schemas } from '../../api/client';
import type { ServerSummary } from './api';

type AckStatus = Schemas['AckStatus'];

export type ServerDisplayState =
  | 'installing'
  | 'restoring'
  | 'deleting'
  | 'installFailed'
  | 'notInstalled'
  | 'starting'
  | 'online'
  | 'stopping'
  | 'offline'
  | 'crashed';

type StatusFields = Pick<ServerSummary, 'installState' | 'runtimeState' | 'activeOperation'>;

/**
 * The single status the UI shows (docs/architecture/05): an operation in progress first, then the install state, then
 * what the agent last observed. Whether the node is connected is shown next to it, not instead of it.
 */
export function serverDisplayState(server: StatusFields): ServerDisplayState {
  switch (server.activeOperation?.type) {
    case 'serverDelete':
      return 'deleting';
    case 'serverProvision':
    case 'serverInstall':
      return 'installing';
    case 'backupRestore':
      return 'restoring';
  }

  if (server.installState === 'failed') {
    return 'installFailed';
  }
  if (server.installState !== 'installed') {
    return 'notInstalled';
  }

  switch (server.runtimeState) {
    case 'running':
      return 'online';
    case 'starting':
    case 'stopping':
    case 'offline':
    case 'crashed':
      return server.runtimeState;
  }
}

export interface ServerActions {
  start: boolean;
  stop: boolean;
  restart: boolean;
  kill: boolean;
  reinstall: boolean;
  delete: boolean;
}

/** Which actions make sense now (permissions are checked separately; the API decides in the end). */
export function serverActions(server: StatusFields & Pick<ServerSummary, 'nodeConnected'>): ServerActions {
  const idle = server.activeOperation === null;
  // A backup reads the files while the game runs: starting and restarting stay possible (docs/architecture/05).
  const free = idle || server.activeOperation?.type === 'backupCreate';
  const connected = server.nodeConnected;
  const stopped = server.runtimeState === 'offline' || server.runtimeState === 'crashed';
  const alive = server.runtimeState === 'starting' || server.runtimeState === 'running';
  return {
    start: connected && free && server.installState === 'installed' && stopped,
    stop: connected && alive,
    restart: connected && free && server.installState === 'installed' && alive,
    kill: connected && (alive || server.runtimeState === 'stopping'),
    reinstall: connected && idle && stopped,
    delete: connected && idle,
  };
}

/**
 * Why the agent refused a command (lifecycle or console): the translation of its code when there is one, the status
 * and code otherwise.
 */
export function commandRefusal(t: TFunction, status: AckStatus, code: string | null): string {
  const generic = t('servers.commandRefused', {
    status: t(`servers.ackStatuses.${status}`, { defaultValue: status }),
    code: code ?? '',
  });
  return code ? t(`errors.${code}`, { defaultValue: generic, detail: '' }) : generic;
}

/**
 * Why an operation or a backup failed, in the user's language: the translation of its code, the agent's own words
 * (often English) when there is none. Translations that need the specifics show the agent's words as {{detail}}.
 */
export function failureReason(t: TFunction, code: string | null, message: string | null): string {
  const words = message ?? code ?? '';
  return code ? t(`errors.${code}`, { defaultValue: words, detail: words }) : words;
}
