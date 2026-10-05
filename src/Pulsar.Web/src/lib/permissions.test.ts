import { describe, expect, it } from 'vitest';
import type { Schemas } from '../api/client';
import { can, canAnywhere, canOnNode, canOnServer } from './permissions';

const node = '0192f1a0-0000-7000-8000-0000000000a1';
const otherNode = '0192f1a0-0000-7000-8000-0000000000a2';
const server = { id: '0192f1a0-0000-7000-8000-0000000000e1', nodeId: otherNode };
const user: Schemas['CurrentUserResponse'] = {
  id: '0192f1a0-0000-7000-8000-000000000001',
  userName: 'host',
  displayName: null,
  email: null,
  permissions: ['audit.view'],
  twoFactorEnabled: false,
  twoFactorSetupRequired: false,
  nodePermissions: [{ id: node, permissions: ['server.view', 'server.start'] }],
  serverPermissions: [{ id: server.id, permissions: ['server.console.view'] }],
};

describe('permissions', () => {
  it('counts a role where it was given, and below it', () => {
    expect(can(user, 'server.start')).toBe(false);
    expect(canOnNode(user, node, 'server.start')).toBe(true);
    expect(canOnNode(user, otherNode, 'server.start')).toBe(false);
    expect(canOnServer(user, { id: 'another', nodeId: node }, 'server.start')).toBe(true); // through its node
    expect(canOnServer(user, server, 'server.console.view')).toBe(true);
    expect(canOnServer(user, server, 'server.start')).toBe(false);
    expect(canOnNode(user, otherNode, 'server.console.view')).toBe(false); // a server role stays on its server
    expect(canOnServer(user, server, 'audit.view')).toBe(true); // global
  });

  it('knows where a permission is held at all, for navigation', () => {
    expect(canAnywhere(user, 'server.view')).toBe(true);
    expect(canAnywhere(user, 'server.console.view')).toBe(true);
    expect(canAnywhere(user, 'server.delete', 'node.view')).toBe(false);
    expect(canAnywhere(null, 'audit.view')).toBe(false);
  });
});
