import { useQuery, useQueryClient } from '@tanstack/react-query';
import { use, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import type { LiveChannel, LiveStatus } from './channel';
import { LiveContext, metricsKey } from './context';
import type { ConsoleEntry, ConsoleOutput, ConsoleSnapshot, ServerMetrics, ServerWatch } from './types';

/** The live channel, or null outside the signed-in area. */
export function useLive(): LiveChannel | null {
  return use(LiveContext);
}

export function useLiveStatus(): LiveStatus | null {
  const live = useLive();
  return useSyncExternalStore(
    (listener) => live?.onStatus(listener) ?? (() => {}),
    () => live?.currentStatus ?? null,
  );
}

/** Pushes changes of every server to the caches while mounted (lists, the dashboard). */
export function useWatchServers(enabled = true) {
  useSharedWatch('servers', (live) => [() => live.invoke('WatchServers'), async () => {}], enabled);
}

/** Pushes changes and usage of every node to the caches while mounted. */
export function useWatchNodes(enabled = true) {
  useSharedWatch('nodes', (live) => [() => live.invoke('WatchNodes'), async () => {}], enabled);
}

/** Usage and progress of one server while mounted; returns its latest usage sample (null when it is not running). */
export function useServerMetrics(serverId: string): ServerMetrics | null {
  const queryClient = useQueryClient();
  useSharedWatch(`server:${serverId}`, (live) => [
    async () => {
      const watch = await live.invoke<ServerWatch | null>('WatchServer', serverId);
      queryClient.setQueryData(metricsKey(serverId), watch?.metrics ?? null);
    },
    () => live.invoke('UnwatchServer', serverId),
  ]);
  const { data } = useQuery<ServerMetrics | null>({
    queryKey: metricsKey(serverId),
    queryFn: () => null,
    enabled: false,
    staleTime: Infinity,
  });
  return data ?? null;
}

export interface ConsoleState {
  /** False until the panel answered the first watch. */
  joined: boolean;
  /** The user may not watch this console. */
  refused: boolean;
}

/**
 * Streams a server's console into <paramref name="write"/>: what the panel holds first, then new output, each
 * sequence once and in order. After a reconnect it asks for what came after the last entry it wrote, so nothing is
 * shown twice or skipped (a gap entry says when the node no longer had some output).
 */
export function useServerConsole(serverId: string, write: (entries: ConsoleEntry[]) => void): ConsoleState {
  const live = useLive();
  const writeRef = useRef(write);
  const [state, setState] = useState<ConsoleState>({ joined: false, refused: false });

  useEffect(() => {
    writeRef.current = write;
  }, [write]);

  useEffect(() => {
    if (!live) {
      return;
    }
    let last: number | null = null;
    let joining = false;
    let pending: ConsoleEntry[] = [];
    let disposed = false;

    const deliver = (entries: ConsoleEntry[]) => {
      const fresh = entries.filter((entry) => last === null || entry.sequence > last);
      const newest = fresh.at(-1);
      if (newest) {
        last = newest.sequence;
        writeRef.current(fresh);
      }
    };
    const offOutput = live.on<[ConsoleOutput]>('ConsoleOutput', (output) => {
      if (output.serverId !== serverId) {
        return;
      }
      if (joining) {
        // Pushed before the answer to the watch arrived: written after what it returns.
        pending.push(...output.entries);
      } else {
        deliver(output.entries);
      }
    });
    const join = async () => {
      joining = true;
      pending = [];
      try {
        const snapshot = await live.invoke<ConsoleSnapshot | null>('WatchConsole', serverId, last);
        if (disposed) {
          return;
        }
        if (snapshot) {
          deliver(snapshot.entries);
        }
        deliver(pending.sort((a, b) => a.sequence - b.sequence));
        setState({ joined: true, refused: !snapshot });
      } catch {
        // The connection dropped meanwhile: the next reconnect joins again.
      } finally {
        joining = false;
        pending = [];
      }
    };

    const offConnected = live.onConnected(() => void join());
    if (live.isConnected) {
      void join();
    }
    return () => {
      disposed = true;
      offOutput();
      offConnected();
      if (live.isConnected) {
        void live.invoke('UnwatchConsole', serverId).catch(() => {});
      }
    };
  }, [live, serverId]);

  return state;
}

function useSharedWatch(
  key: string,
  create: (live: LiveChannel) => [() => Promise<unknown>, () => Promise<unknown>],
  enabled = true,
) {
  const live = useLive();
  const createRef = useRef(create);
  useEffect(() => {
    createRef.current = create;
  }, [create]);
  useEffect(() => {
    if (!live || !enabled) {
      return;
    }
    const [watch, unwatch] = createRef.current(live);
    return live.watch(key, watch, unwatch);
  }, [live, key, enabled]);
}
