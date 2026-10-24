import { describe, expect, it } from 'vitest';
import {
  checkPort,
  checkPorts,
  parsePort,
  portsReady,
  suggestAllocations,
  suggestNumbers,
  type NodeAllocation,
  type TemplatePort,
} from './portChoices';

const port = (overrides: Partial<TemplatePort>): TemplatePort => ({
  name: 'game',
  label: 'Game',
  protocols: ['udp'],
  default: 27015,
  required: true,
  description: null,
  ...overrides,
});

let next = 0;
const allocation = (portNumber: number, overrides: Partial<NodeAllocation> = {}): NodeAllocation => ({
  id: `a${++next}`,
  ipAddress: '0.0.0.0',
  port: portNumber,
  inUse: false,
  serverId: null,
  serverName: null,
  ...overrides,
});

const sourceGame = [port({}), port({ name: 'tv', label: 'SourceTV', default: 27020, required: false })];
const valheim = [
  port({ default: 2456 }),
  port({ name: 'query', label: 'Steam query', default: 2457, follows: 'game', offset: 1 }),
];

describe('parsePort', () => {
  it.each([
    ['27015', 27015],
    [' 1 ', 1],
    ['65535', 65535],
    ['0', null],
    ['65536', null],
    ['1e3', null],
    ['-5', null],
    ['', null],
  ])('reads %j as %j', (value, expected) => {
    expect(parsePort(value)).toBe(expected);
  });
});

describe('checkPort', () => {
  it("uses the node's free allocation on that number, every interface first", () => {
    const allocations = [allocation(27015, { ipAddress: '10.0.0.2' }), allocation(27015, { ipAddress: '0.0.0.0' })];
    // Two allocations of one port on 0.0.0.0 and 10.0.0.2 cannot both exist; the order still says which one wins.
    expect(checkPort(27015, null, allocations, false)).toEqual({ kind: 'existing', address: '0.0.0.0' });
  });

  it("keeps a server's own allocation its own", () => {
    const allocations = [allocation(27015, { inUse: true, serverId: 's1', serverName: 'Retakes' })];
    expect(checkPort(27015, null, allocations, true, 's1')).toEqual({ kind: 'existing', address: '0.0.0.0' });
    expect(checkPort(27015, null, allocations, true, 's2')).toEqual({ kind: 'taken', serverName: 'Retakes' });
    expect(checkPort(27015, null, allocations, true)).toEqual({ kind: 'taken', serverName: 'Retakes' });
  });

  it('does not take a server the user cannot see for their own', () => {
    // Held by a hidden server: no id, no name.
    const allocations = [allocation(27025, { inUse: true })];
    expect(checkPort(27025, null, allocations, true, 's1')).toEqual({ kind: 'taken', serverName: null });
  });

  it('adds a number the node has no allocation for, when the user manages it', () => {
    expect(checkPort(27016, null, [], true)).toEqual({ kind: 'new', address: '0.0.0.0' });
    expect(checkPort(27016, null, [], false)).toEqual({ kind: 'notAllocated' });
  });

  it("keeps a following port on its port's address", () => {
    const allocations = [allocation(2457, { ipAddress: '10.0.0.2' })];
    expect(checkPort(2457, '10.0.0.2', allocations, true)).toEqual({ kind: 'existing', address: '10.0.0.2' });
    // A free allocation of another address that a new one would overlap.
    expect(checkPort(2457, '10.0.0.3', [allocation(2457)], true)).toEqual({ kind: 'otherAddress' });
    expect(checkPort(2457, '10.0.0.3', allocations, true)).toEqual({ kind: 'new', address: '10.0.0.3' });
  });
});

describe('checkPorts', () => {
  it('checks the ports that follow one on the next numbers', () => {
    const allocations = [allocation(2501, { inUse: true, serverId: 's9', serverName: 'Other' })];
    const [game] = checkPorts(valheim, { game: '2500' }, allocations, true);

    expect(game?.check).toEqual({ kind: 'new', address: '0.0.0.0' });
    expect(game?.followers).toEqual([
      { port: valheim[1], number: 2501, check: { kind: 'taken', serverName: 'Other' } },
    ]);
    expect(portsReady(checkPorts(valheim, { game: '2500' }, allocations, true))).toBe(false);
    expect(portsReady(checkPorts(valheim, { game: '2502' }, allocations, true))).toBe(true);
  });

  it('refuses two ports of the server on one number, a follower past 65535, and nothing for a required port', () => {
    expect(checkPorts(sourceGame, { game: '27015', tv: '27015' }, [], true).map((c) => c.check.kind)).toEqual([
      'duplicate',
      'duplicate',
    ]);
    expect(checkPorts(valheim, { game: '2456' }, [], true).length).toBe(1);
    expect(checkPorts(valheim, { game: '2457', query: '' }, [allocation(2457)], true)[0]?.followers[0]?.check).toEqual({
      kind: 'new',
      address: '0.0.0.0',
    });
    expect(checkPorts(valheim, { game: '65535' }, [], true)[0]?.followers[0]?.check).toEqual({ kind: 'invalid' });
    expect(checkPorts(sourceGame, { game: '', tv: '' }, [], true).map((c) => c.check.kind)).toEqual([
      'required',
      'off',
    ]);
    expect(checkPorts(sourceGame, { game: '70000' }, [], true)[0]?.check).toEqual({ kind: 'invalid' });
  });
});

describe('suggestNumbers', () => {
  it("starts on the game's own ports, the next free ones for a second server, optional ports off", () => {
    expect(suggestNumbers(sourceGame, [])).toEqual({ game: '27015', tv: '' });
    const first = [allocation(27015, { inUse: true, serverId: 's1', serverName: 'First' })];
    expect(suggestNumbers(sourceGame, first)).toEqual({ game: '27016', tv: '' });
  });

  it('moves a port with followers up until they all fit', () => {
    // The first server's query port is taken: 2457 cannot be the game port either, nor 2456 (its query would be 2457).
    const taken = [allocation(2457, { inUse: true, serverId: 's1', serverName: 'First' })];
    expect(suggestNumbers(valheim, taken)).toEqual({ game: '2458' });
  });
});

describe('suggestAllocations', () => {
  it("prefers the game's own port, then the nearest above it, then the lowest below", () => {
    const own = allocation(27015);
    const above = allocation(27100);
    const below = allocation(26000);
    expect(suggestAllocations(sourceGame, [above, below, own])).toEqual({ game: own.id, tv: '' });
    expect(suggestAllocations(sourceGame, [above, below])).toEqual({ game: above.id, tv: '' });
    expect(suggestAllocations(sourceGame, [below, allocation(27015, { inUse: true })])).toEqual({
      game: below.id,
      tv: '',
    });
  });

  it('needs the following ports free on the same address', () => {
    const lone = allocation(2456);
    const pair = [allocation(2470), allocation(2471)];
    expect(suggestAllocations(valheim, [lone, ...pair])).toEqual({ game: pair[0]?.id });
    expect(suggestAllocations(valheim, [lone])).toEqual({ game: '' });
  });
});
