import type { Schemas } from '../../api/client';

/** Messages of the live channel (src/Pulsar.Api/Realtime/PanelClient.cs), as JSON. */

export interface ConsoleEntry {
  /** Increases by one per entry; a restarted agent continues above earlier sequences. */
  sequence: number;
  at: string;
  text: string;
  /** Output up to this sequence was lost (the node's buffer had moved on). */
  isGap: boolean;
}

export interface ConsoleSnapshot {
  entries: ConsoleEntry[];
  nodeConnected: boolean;
}

export interface ConsoleOutput {
  serverId: string;
  entries: ConsoleEntry[];
}

export interface ServerMetrics {
  serverId: string;
  at: string;
  /** Share of the node's whole CPU (0–100). */
  cpuPercent: number;
  memoryBytes: number;
  processCount: number;
  uptimeSeconds: number;
}

export interface ServerWatch {
  metrics: ServerMetrics | null;
}

export interface OperationProgress {
  serverId: string;
  operationId: string;
  stage: string;
  percent: number | null;
}

export interface NodeMetricsUpdate {
  nodeId: string;
  metrics: Schemas['NodeMetricsView'];
}
