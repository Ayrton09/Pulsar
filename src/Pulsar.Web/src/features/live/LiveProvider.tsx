import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useEffect, useState, type ReactNode } from 'react';
import { adminKeys } from '../admin/api';
import { nodeKeys, type NodeDetails, type NodeSummary } from '../nodes/api';
import { serverKeys, type ServerDetails, type ServerSummary } from '../servers/api';
import { LiveChannel } from './channel';
import { LiveContext, metricsKey } from './context';
import type { NodeMetricsUpdate, OperationProgress, ServerMetrics } from './types';

/**
 * Opens the tab's live channel while someone is signed in, and turns its notices into cache updates: changed servers
 * and nodes are fetched again, progress and usage are written in place. After a reconnection everything shown is
 * fetched again, since notices sent while disconnected are lost.
 */
export function LiveProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [live] = useState(() => new LiveChannel());

  useEffect(() => {
    // Servers and their lists; not their settings, which change only when someone saves them.
    const refreshServers = () => {
      void queryClient.invalidateQueries({
        queryKey: serverKeys.all,
        predicate: (query) => query.queryKey[2] !== 'settings',
      });
      // The dashboard's latest activity: what changes servers and nodes is audited.
      void queryClient.invalidateQueries({ queryKey: [...adminKeys.audit, 'recent'] });
    };
    let reconnect = false;
    const offs = [
      live.on<[string[]]>('ServersChanged', refreshServers),
      live.on<[string[]]>('NodesChanged', () => {
        void queryClient.invalidateQueries({ queryKey: nodeKeys.all });
        refreshServers(); // servers show whether their node is connected
      }),
      live.on<[OperationProgress]>('OperationProgress', (progress) => applyProgress(queryClient, progress)),
      live.on<[ServerMetrics]>('ServerMetrics', (metrics) =>
        queryClient.setQueryData(metricsKey(metrics.serverId), metrics),
      ),
      live.on<[NodeMetricsUpdate]>('NodeMetrics', (update) => applyNodeMetrics(queryClient, update)),
      // What changed while disconnected was never pushed. (The first connection follows the page's own fetches.)
      live.onConnected(() => {
        if (reconnect) {
          void queryClient.invalidateQueries();
        }
        reconnect = true;
      }),
    ];
    live.start();
    return () => {
      offs.forEach((off) => off());
      void live.stop();
    };
  }, [live, queryClient]);

  return <LiveContext value={live}>{children}</LiveContext>;
}

function applyProgress(queryClient: QueryClient, progress: OperationProgress) {
  const patch = (server: ServerSummary): ServerSummary =>
    server.id === progress.serverId && server.activeOperation?.id === progress.operationId
      ? { ...server, activeOperation: { ...server.activeOperation, stage: progress.stage, percent: progress.percent } }
      : server;
  queryClient.setQueryData<ServerDetails>(serverKeys.detail(progress.serverId), (details) =>
    details ? { ...details, server: patch(details.server) } : details,
  );
  queryClient.setQueryData<ServerSummary[]>(serverKeys.all, (servers) => servers?.map(patch));
}

function applyNodeMetrics(queryClient: QueryClient, { nodeId, metrics }: NodeMetricsUpdate) {
  const patch = (node: NodeSummary): NodeSummary => (node.id === nodeId ? { ...node, metrics } : node);
  queryClient.setQueryData<NodeSummary[]>(nodeKeys.all, (nodes) => nodes?.map(patch));
  queryClient.setQueryData<NodeDetails>(nodeKeys.detail(nodeId), (details) =>
    details ? { ...details, node: patch(details.node) } : details,
  );
}
