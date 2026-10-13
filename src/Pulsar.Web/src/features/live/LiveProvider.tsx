import { useQueryClient, type Query, type QueryClient } from '@tanstack/react-query';
import { useEffect, useState, type ReactNode } from 'react';
import { adminKeys } from '../admin/api';
import { sessionKey } from '../auth/api';
import { fileKeys } from '../files/api';
import { nodeKeys, panelKeysKey, type NodeDetails, type NodeSummary } from '../nodes/api';
import { serverKeys, type ServerDetails, type ServerSummary } from '../servers/api';
import { LiveChannel } from './channel';
import { LiveContext, metricsKey } from './context';
import type { NodeMetricsUpdate, OperationProgress, ServerMetrics } from './types';

/**
 * Opens the tab's live channel while someone is signed in, and turns its notices into cache updates: changed servers
 * and nodes are fetched again, progress and usage are written in place. After a reconnection everything shown is
 * fetched again, since notices sent while disconnected are lost. While it cannot connect, the session is read again:
 * when it ended, the route guard sends the user to sign in.
 */
export function LiveProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [live] = useState(() => new LiveChannel());

  useEffect(() => {
    // Servers and their lists; not their settings, which change only when someone saves them.
    const refreshServers = () => {
      void queryClient.invalidateQueries({ queryKey: serverKeys.all, predicate: notServerSettings });
      // The dashboard's latest activity: what changes servers and nodes is audited.
      void queryClient.invalidateQueries({ queryKey: [...adminKeys.audit, 'recent'] });
    };
    let reconnect = false;
    const offs = [
      live.on<[string[]]>('ServersChanged', (serverIds) => {
        refreshServers();
        // An install, an update or a restore rewrites the files: the listings of the servers named (the open editor's
        // content is not a query). Not at every node notice, which names no server.
        for (const id of serverIds) {
          void queryClient.invalidateQueries({ queryKey: fileKeys.all(id) });
        }
      }),
      live.on<[string[]]>('NodesChanged', () => {
        void queryClient.invalidateQueries({ queryKey: nodeKeys.all });
        // Who trusts which panel key, and how many times a reusable token was used, change with the nodes.
        void queryClient.invalidateQueries({ queryKey: panelKeysKey });
        void queryClient.invalidateQueries({ queryKey: nodeKeys.enrollmentTokens });
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
          void queryClient.invalidateQueries({ predicate: notServerSettings });
        }
        reconnect = true;
      }),
      // The panel refuses a session that ended; a failed fetch keeps the page (the guard shows a notice).
      live.onFailure(() => void queryClient.invalidateQueries({ queryKey: sessionKey })),
    ];
    live.start();
    return () => {
      offs.forEach((off) => off());
      void live.stop();
    };
  }, [live, queryClient]);

  return <LiveContext value={live}>{children}</LiveContext>;
}

/** A refetch of a server's settings would drop an admin's unsaved edits; only a save or a template upgrade changes them. */
function notServerSettings(query: Query): boolean {
  return !(query.queryKey[0] === serverKeys.all[0] && query.queryKey[2] === 'settings');
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
