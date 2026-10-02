import type { NodeSummary } from './api';

export type NodeState = 'online' | 'offline' | 'pending' | 'disabled';

/** What the UI shows for a node: its administrative status first, then whether its agent is connected. */
export function nodeState(node: Pick<NodeSummary, 'status' | 'connected'>): NodeState {
  if (node.status === 'disabled') {
    return 'disabled';
  }
  if (node.status === 'pendingEnrollment') {
    return 'pending';
  }
  return node.connected ? 'online' : 'offline';
}

/**
 * The command an administrator runs on the Windows machine. The panel URL is the one this browser uses, which is what
 * agents should use too (the panel binds signatures to it).
 */
export function enrollCommand(origin: string, token: string, panelKeyFingerprint: string): string {
  return `.\\PulsarAgent.exe enroll --panel ${origin} --token ${token} --panel-key ${panelKeyFingerprint}`;
}
