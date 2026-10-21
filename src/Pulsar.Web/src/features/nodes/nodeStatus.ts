import { canOnNode, Permission } from '../../lib/permissions';
import type { CurrentUser } from '../auth/api';
import type { NodeSummary } from './api';

/**
 * Whether the user sees the node's machine, usage and page (node.view or node.manage on it). The list also carries the
 * nodes they may only create servers on, with their name and state alone: those are for the create dialog.
 */
export function seesNodeDetails(user: CurrentUser, node: Pick<NodeSummary, 'id'>): boolean {
  return canOnNode(user, node.id, Permission.NodeView, Permission.NodeManage);
}

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
  return `powershell -ExecutionPolicy Bypass -File .\\Install-Pulsar.ps1 -Mode Agent -PanelUrl ${argument(origin)} -Token ${argument(token)} -PanelKey ${argument(panelKeyFingerprint)}`;
}

/** The agent's MSI, installed silently (Intune, a group policy, a script) with a reusable token. */
export function msiCommand(origin: string, token: string, panelKeyFingerprint: string, agentPackage: string): string {
  return `msiexec /i ${argument(agentPackage)} /qn PANELURL=${argument(origin)} TOKEN=${argument(token)} PANELKEY=${argument(panelKeyFingerprint)}`;
}

/**
 * One argument of a command pasted into a console (cmd or PowerShell), whatever the panel's answer holds: as it is when
 * plain (what the panel sends: URLs, tokens, fingerprints, file names), otherwise in double quotes, without the
 * characters that would end them, expand in them or run something else.
 */
function argument(value: string): string {
  if (/^[A-Za-z0-9._:/@+=-]+$/.test(value)) {
    return value;
  }
  // Format characters too: they could make the command read as something else than it runs.
  return `"${value.replace(/["`$%!&|<>^;(){}\r\n\p{Cc}\p{Cf}]/gu, '')}"`;
}

/** Where the panel serves the agent's setup, when it has it (the enrollment says so: agentSetupAvailable). */
export const agentSetupPath = '/downloads/PulsarAgentSetup.exe';
