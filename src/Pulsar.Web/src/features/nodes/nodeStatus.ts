import type { NodeSummary } from './api';

export type NodeState = 'online' | 'offline' | 'pending' | 'awaiting' | 'disabled';

/** What the UI shows for a node: its administrative status first, then whether its agent is connected. */
export function nodeState(node: Pick<NodeSummary, 'status' | 'connected'>): NodeState {
  if (node.status === 'disabled') {
    return 'disabled';
  }
  if (node.status === 'pendingEnrollment') {
    return 'pending';
  }
  if (node.status === 'pendingApproval') {
    return 'awaiting';
  }
  return node.connected ? 'online' : 'offline';
}

/**
 * The command an administrator runs on the Windows machine. The panel URL is the one this browser uses, which is what
 * agents should use too (the panel binds signatures to it).
 */
/**
 * What installs the agent on a node, run in the release ZIP's folder: it copies the agent to Program Files (a service
 * must not run from a folder other accounts can write) and enrolls it.
 */
export function enrollCommand(origin: string, token: string, panelKeyFingerprint: string): string {
  return `powershell -ExecutionPolicy Bypass -File .\\Install-Pulsar.ps1 -Mode Agent -PanelUrl ${origin} -Token ${token} -PanelKey ${panelKeyFingerprint}`;
}

/** The agent's MSI, installed silently (Intune, a group policy, a script) with a reusable token. */
export function msiCommand(origin: string, token: string, panelKeyFingerprint: string, agentPackage: string): string {
  return `msiexec /i ${agentPackage} /qn PANELURL=${origin} TOKEN=${token} PANELKEY=${panelKeyFingerprint}`;
}
