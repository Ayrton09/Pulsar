/**
 * Stands in for @microsoft/signalr in unit tests (test/setup.ts): the live channel connects at once, its calls are
 * recorded and answered from `answers`, and tests push messages with `emit`.
 */
type Callback = (...args: unknown[]) => void;

export const HubConnectionState = {
  Disconnected: 'Disconnected',
  Connecting: 'Connecting',
  Connected: 'Connected',
  Disconnecting: 'Disconnecting',
  Reconnecting: 'Reconnecting',
} as const;

export const HttpTransportType = { None: 0, WebSockets: 1, ServerSentEvents: 2, LongPolling: 4 } as const;

export const LogLevel = { Trace: 0, Debug: 1, Information: 2, Warning: 3, Error: 4, Critical: 5, None: 6 } as const;

/** What the panel answers by default: every watch allowed, nothing to show yet. */
const defaults: Record<string, (...args: unknown[]) => unknown> = {
  WatchServers: () => true,
  WatchNodes: () => true,
  WatchServer: () => ({ metrics: null }),
  WatchConsole: () => ({ entries: [], nodeConnected: true }),
};

export class FakeHubConnection {
  state: string = HubConnectionState.Disconnected;
  readonly invocations: { method: string; args: unknown[] }[] = [];
  answers: Record<string, (...args: unknown[]) => unknown> = {};
  private readonly handlers = new Map<string, Set<Callback>>();
  private readonly reconnecting = new Set<Callback>();
  private readonly reconnected = new Set<Callback>();
  private readonly closed = new Set<Callback>();

  start(): Promise<void> {
    this.state = HubConnectionState.Connected;
    return Promise.resolve();
  }

  stop(): Promise<void> {
    this.state = HubConnectionState.Disconnected;
    return Promise.resolve();
  }

  on(method: string, callback: Callback) {
    const set = this.handlers.get(method) ?? new Set<Callback>();
    set.add(callback);
    this.handlers.set(method, set);
  }

  off(method: string, callback: Callback) {
    this.handlers.get(method)?.delete(callback);
  }

  invoke(method: string, ...args: unknown[]): Promise<unknown> {
    this.invocations.push({ method, args });
    const answer = this.answers[method] ?? defaults[method];
    return Promise.resolve(answer?.(...args));
  }

  onreconnecting(callback: Callback) {
    this.reconnecting.add(callback);
  }

  onreconnected(callback: Callback) {
    this.reconnected.add(callback);
  }

  onclose(callback: Callback) {
    this.closed.add(callback);
  }

  /** A message from the panel. */
  emit(method: string, ...args: unknown[]) {
    for (const handler of this.handlers.get(method) ?? []) {
      handler(...args);
    }
  }

  /** The connection drops; `reconnect()` brings it back. */
  drop() {
    this.state = HubConnectionState.Reconnecting;
    this.reconnecting.forEach((callback) => callback());
  }

  reconnect() {
    this.state = HubConnectionState.Connected;
    this.reconnected.forEach((callback) => callback());
  }

  calls(method: string) {
    return this.invocations.filter((call) => call.method === method);
  }
}

/** Every connection built, newest last. */
export const connections: FakeHubConnection[] = [];

let nextAnswers: Record<string, (...args: unknown[]) => unknown> = {};

/** Answers for the next connection the app opens (it opens once the session is loaded). */
export function answerNext(answers: Record<string, (...args: unknown[]) => unknown>) {
  nextAnswers = answers;
}

/** The connection opened after <paramref name="before"/> connections existed (waits for the app to open it). */
export async function openedConnection(before: number): Promise<FakeHubConnection> {
  for (let attempt = 0; attempt < 200; attempt++) {
    const connection = connections[before];
    if (connection) {
      return connection;
    }
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  throw new Error('The app opened no live connection.');
}

export class HubConnectionBuilder {
  withUrl() {
    return this;
  }

  withAutomaticReconnect() {
    return this;
  }

  configureLogging() {
    return this;
  }

  build() {
    const connection = new FakeHubConnection();
    connection.answers = nextAnswers;
    nextAnswers = {};
    connections.push(connection);
    return connection;
  }
}
