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

interface RetryPolicy {
  nextRetryDelayInMilliseconds: (context: {
    previousRetryCount: number;
    elapsedMilliseconds: number;
    retryReason: Error;
  }) => number | null;
}

export class FakeHubConnection {
  state: string = HubConnectionState.Disconnected;
  readonly invocations: { method: string; args: unknown[] }[] = [];
  answers: Record<string, (...args: unknown[]) => unknown> = {};
  /** Connecting fails while set (a proxy refusing the upgrade, the panel refusing a session that ended). */
  refuse = false;
  retryPolicy: RetryPolicy | undefined;
  private retries = 0;
  private readonly handlers = new Map<string, Set<Callback>>();
  private readonly reconnecting = new Set<Callback>();
  private readonly reconnected = new Set<Callback>();
  private readonly closed = new Set<Callback>();

  start(): Promise<void> {
    if (this.refuse) {
      return Promise.reject(new Error('The connection was refused.'));
    }
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

  /** The connection drops; `reconnect()` brings it back, `failReconnect()` is one attempt that fails. */
  drop() {
    this.state = HubConnectionState.Reconnecting;
    this.retries = 0;
    // Like SignalR: the policy is asked for the first delay before the reconnecting event.
    this.nextRetryDelay();
    this.reconnecting.forEach((callback) => callback());
  }

  /** An attempt to reconnect fails: SignalR asks the policy when to try again. */
  failReconnect() {
    this.retries++;
    this.nextRetryDelay();
  }

  private nextRetryDelay() {
    this.retryPolicy?.nextRetryDelayInMilliseconds({
      previousRetryCount: this.retries,
      elapsedMilliseconds: 0,
      retryReason: new Error('The connection was lost.'),
    });
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
let refuseNextConnection = false;

/** Answers for the next connection the app opens (it opens once the session is loaded). */
export function answerNext(answers: Record<string, (...args: unknown[]) => unknown>) {
  nextAnswers = answers;
}

/** The next connection the app opens cannot connect until the test clears its `refuse`. */
export function refuseNext() {
  refuseNextConnection = true;
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
  private retryPolicy: RetryPolicy | undefined;

  withUrl() {
    return this;
  }

  withAutomaticReconnect(policy?: RetryPolicy) {
    this.retryPolicy = policy;
    return this;
  }

  configureLogging() {
    return this;
  }

  build() {
    const connection = new FakeHubConnection();
    connection.answers = nextAnswers;
    connection.retryPolicy = this.retryPolicy;
    connection.refuse = refuseNextConnection;
    nextAnswers = {};
    refuseNextConnection = false;
    connections.push(connection);
    return connection;
  }
}
