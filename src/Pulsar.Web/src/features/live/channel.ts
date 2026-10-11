import {
  HttpTransportType,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr';

export type LiveStatus = 'connecting' | 'connected' | 'reconnecting';

/** Quick retries first, then every 30 s, for as long as the page is open (the panel may be restarting). */
const retryDelay = (attempt: number) => Math.min(30_000, 500 * 2 ** attempt);

interface Watch {
  count: number;
  watch: () => Promise<unknown>;
  unwatch: () => Promise<unknown>;
}

/**
 * The tab's one connection to the panel's live channel (/hubs/panel, docs/architecture/07): pushed changes, usage and
 * console output. Pages say what they watch; watches are shared and reference counted, and sent again after every
 * reconnect (the panel forgets them with the old connection).
 */
export class LiveChannel {
  private readonly connection: HubConnection;
  private readonly watches = new Map<string, Watch>();
  private readonly statusListeners = new Set<() => void>();
  private readonly connectedListeners = new Set<() => void>();
  private readonly failureListeners = new Set<() => void>();
  private status: LiveStatus = 'connecting';
  private stopped = false;
  private retry: ReturnType<typeof setTimeout> | undefined;

  constructor(url = '/hubs/panel') {
    this.connection = new HubConnectionBuilder()
      .withUrl(url, { transport: HttpTransportType.WebSockets, skipNegotiation: true })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (context) => {
          // Asked first when the connection drops, then after each attempt that failed.
          if (context.previousRetryCount > 0) {
            this.failed();
          }
          return retryDelay(context.previousRetryCount);
        },
      })
      .configureLogging(LogLevel.None)
      .build();
    this.connection.onreconnecting(() => {
      this.setStatus('reconnecting');
      this.failed();
    });
    this.connection.onreconnected(() => this.connected());
    // Closed without reconnecting (the panel closed it): start over, unless the page is done with it.
    this.connection.onclose(() => {
      if (!this.stopped) {
        this.setStatus('reconnecting');
        this.schedule(1);
      }
    });
  }

  get currentStatus(): LiveStatus {
    return this.status;
  }

  get isConnected(): boolean {
    return this.connection.state === HubConnectionState.Connected;
  }

  /** Connects, and keeps reconnecting until stopped. It can start again after a stop (React mounts twice in development). */
  start(): void {
    this.stopped = false;
    void this.connect(0);
  }

  async stop(): Promise<void> {
    this.stopped = true;
    clearTimeout(this.retry);
    await this.connection.stop();
  }

  onStatus(listener: () => void): () => void {
    this.statusListeners.add(listener);
    return () => this.statusListeners.delete(listener);
  }

  /**
   * Runs when the connection drops and after every attempt to connect that fails. The panel refuses a session that
   * ended (it expired, or the user signed out in another tab), but a refused connection does not say why: the session
   * does.
   */
  onFailure(listener: () => void): () => void {
    this.failureListeners.add(listener);
    return () => this.failureListeners.delete(listener);
  }

  /** Runs after every (re)connection, once the shared watches were sent again. */
  onConnected(listener: () => void): () => void {
    this.connectedListeners.add(listener);
    return () => this.connectedListeners.delete(listener);
  }

  on<T extends unknown[]>(method: string, handler: (...args: T) => void): () => void {
    const callback = handler as (...args: unknown[]) => void;
    this.connection.on(method, callback);
    return () => this.connection.off(method, callback);
  }

  invoke<T>(method: string, ...args: unknown[]): Promise<T> {
    return this.connection.invoke<T>(method, ...args);
  }

  /**
   * A watch several components may share (the server list, a server): <paramref name="watch"/> runs when the first
   * one starts and after every reconnect, <paramref name="unwatch"/> when the last one stops. Returns the stop.
   */
  watch(key: string, watch: () => Promise<unknown>, unwatch: () => Promise<unknown>): () => void {
    let entry = this.watches.get(key);
    if (!entry) {
      entry = { count: 0, watch, unwatch };
      this.watches.set(key, entry);
    }
    entry.count++;
    if (entry.count === 1 && this.isConnected) {
      void entry.watch().catch(ignore);
    }
    const current = entry;
    return () => {
      current.count--;
      if (current.count === 0 && this.watches.get(key) === current) {
        this.watches.delete(key);
        if (this.isConnected) {
          void current.unwatch().catch(ignore);
        }
      }
    };
  }

  private async connect(attempt: number): Promise<void> {
    if (this.stopped) {
      return;
    }
    if (this.connection.state !== HubConnectionState.Disconnected) {
      // Still closing after a stop: try once it is done. Already connected or connecting: nothing to do.
      if (this.connection.state === HubConnectionState.Disconnecting) {
        this.schedule(attempt);
      }
      return;
    }
    try {
      await this.connection.start();
      this.connected();
    } catch {
      if (this.stopped) {
        return;
      }
      // Also when the first connection never succeeds (a proxy refusing WebSockets): pages are not being updated.
      this.setStatus('reconnecting');
      this.failed();
      this.schedule(attempt + 1);
    }
  }

  private schedule(attempt: number) {
    clearTimeout(this.retry);
    this.retry = setTimeout(() => void this.connect(attempt), retryDelay(attempt));
  }

  private connected() {
    if (this.stopped) {
      return;
    }
    this.setStatus('connected');
    for (const entry of this.watches.values()) {
      void entry.watch().catch(ignore);
    }
    for (const listener of this.connectedListeners) {
      listener();
    }
  }

  private failed() {
    if (this.stopped) {
      return;
    }
    for (const listener of this.failureListeners) {
      listener();
    }
  }

  private setStatus(status: LiveStatus) {
    if (this.status !== status) {
      this.status = status;
      for (const listener of this.statusListeners) {
        listener();
      }
    }
  }
}

function ignore() {
  // A watch that fails is sent again at the next reconnect; the page still has its last data.
}
