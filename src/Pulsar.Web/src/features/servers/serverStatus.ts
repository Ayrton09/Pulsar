import type { ServerSummary } from './api';

export type ServerDisplayState =
  | 'installing'
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
  const connected = server.nodeConnected;
  const stopped = server.runtimeState === 'offline' || server.runtimeState === 'crashed';
  const alive = server.runtimeState === 'starting' || server.runtimeState === 'running';
  return {
    start: connected && idle && server.installState === 'installed' && stopped,
    stop: connected && alive,
    restart: connected && idle && server.installState === 'installed' && alive,
    kill: connected && (alive || server.runtimeState === 'stopping'),
    reinstall: connected && idle && stopped,
    delete: connected && idle,
  };
}
