import type { Schemas } from '../../api/client';

export type TemplatePort = Schemas['TemplatePort'];
export type NodeAllocation = Pick<
  Schemas['AllocationView'],
  'id' | 'ipAddress' | 'port' | 'inUse' | 'serverId' | 'serverName'
>;
/** A port another program holds on the node's machine (1.0.7 agents). */
export type PortInUse = Schemas['PortInUse'];

const anyAddress = '0.0.0.0';

/**
 * What a port number would be for a server, as the panel decides it (ServerSpecs.AllocationsForNumbersAsync): one of
 * the node's allocations on that number (the server's own, else a free one, every interface first), else a new one
 * when the user manages the node.
 */
export type PortCheck =
  | { kind: 'existing'; address: string }
  | { kind: 'new'; address: string }
  | { kind: 'taken'; serverName: string | null }
  | { kind: 'busy' }
  | { kind: 'otherAddress' }
  | { kind: 'notAllocated' }
  | { kind: 'invalid' }
  | { kind: 'duplicate' }
  | { kind: 'required' }
  | { kind: 'off' };

export const usable = (check: PortCheck) => check.kind === 'existing' || check.kind === 'new';

/** A chosen port and the ports that come with it (on the next numbers, on its address). */
export interface PortChoice {
  port: TemplatePort;
  number: number | null;
  check: PortCheck;
  followers: { port: TemplatePort; number: number | null; check: PortCheck }[];
}

/** Ports a user chooses: the others come with the port they follow. */
export const chosenPorts = (ports: TemplatePort[]) => ports.filter((port) => !port.follows);

const followersOf = (ports: TemplatePort[], name: string) => ports.filter((port) => port.follows === name);

/** "27015" as a port number; null for anything else (empty, "1e3", "70000"). */
export function parsePort(value: string): number | null {
  const text = value.trim();
  if (!/^\d{1,5}$/.test(text)) {
    return null;
  }
  const port = Number(text);
  return port >= 1 && port <= 65535 ? port : null;
}

/**
 * Where `port` on `address` (null: any) would land, `serverId` being the server's own when it exists already. A
 * number another program holds (`inUse`, on one of the port's `protocols`) is busy, unless it is the server's own.
 */
export function checkPort(
  port: number,
  address: string | null,
  allocations: NodeAllocation[],
  canAdd: boolean,
  serverId?: string,
  inUse: PortInUse[] = [],
  protocols: string[] = [],
): PortCheck {
  const onPort = allocations.filter((a) => a.port === port);
  const mine = (a: NodeAllocation) => serverId !== undefined && a.serverId === serverId;
  // The address is left out: a program on one interface still keeps a game on every interface from starting.
  const busy = inUse.some((p) => p.port === port && protocols.includes(p.protocol));
  const found = onPort
    .filter((a) => (!a.inUse || mine(a)) && (address === null || a.ipAddress === address))
    .sort(
      (a, b) =>
        Number(!mine(a)) - Number(!mine(b)) ||
        Number(a.ipAddress !== anyAddress) - Number(b.ipAddress !== anyAddress) ||
        a.ipAddress.localeCompare(b.ipAddress),
    )[0];
  if (found) {
    return busy && !mine(found) ? { kind: 'busy' } : { kind: 'existing', address: found.ipAddress };
  }
  const wanted = address ?? anyAddress;
  const overlapping = onPort.filter(
    (a) => a.ipAddress === wanted || a.ipAddress === anyAddress || wanted === anyAddress,
  );
  const taken = overlapping.find((a) => a.inUse && !mine(a));
  if (taken) {
    return { kind: 'taken', serverName: taken.serverName };
  }
  if (busy) {
    return { kind: 'busy' };
  }
  if (overlapping.length > 0) {
    return { kind: 'otherAddress' };
  }
  return canAdd ? { kind: 'new', address: wanted } : { kind: 'notAllocated' };
}

/** Every chosen port with what its number (`values`, as typed) and its followers' would be. */
export function checkPorts(
  ports: TemplatePort[],
  values: Record<string, string>,
  allocations: NodeAllocation[],
  canAdd: boolean,
  serverId?: string,
  inUse: PortInUse[] = [],
): PortChoice[] {
  const choices = chosenPorts(ports).map((port): PortChoice => {
    const value = values[port.name] ?? '';
    const number = parsePort(value);
    const followers = followersOf(ports, port.name).map((follower) => ({
      port: follower,
      number: number === null ? null : number + (follower.offset ?? 1),
    }));
    if (value.trim() === '') {
      const check: PortCheck = port.required ? { kind: 'required' } : { kind: 'off' };
      return { port, number: null, check, followers: followers.map((f) => ({ ...f, number: null, check })) };
    }
    if (number === null) {
      const check: PortCheck = { kind: 'invalid' };
      return { port, number, check, followers: followers.map((f) => ({ ...f, check })) };
    }
    const check = checkPort(number, null, allocations, canAdd, serverId, inUse, port.protocols);
    const address = check.kind === 'existing' || check.kind === 'new' ? check.address : null;
    return {
      port,
      number,
      check,
      followers: followers.map((f) => ({
        ...f,
        check:
          f.number === null || f.number > 65535
            ? { kind: 'invalid' }
            : checkPort(f.number, address, allocations, canAdd, serverId, inUse, f.port.protocols),
      })),
    };
  });

  // Two ports of one server never share a number.
  const numbers = choices.flatMap((c) => [c.number, ...c.followers.map((f) => f.number)]).filter((n) => n !== null);
  const repeated = (n: number | null) => n !== null && numbers.filter((other) => other === n).length > 1;
  return choices.map((choice) => ({
    ...choice,
    check: repeated(choice.number) ? { kind: 'duplicate' } : choice.check,
    followers: choice.followers.map((f) => (repeated(f.number) ? { ...f, check: { kind: 'duplicate' } } : f)),
  }));
}

/** Whether the server can be given these ports: every one usable, or off when optional. */
export const portsReady = (choices: PortChoice[]) =>
  choices.every((c) => c.check.kind === 'off' || (usable(c.check) && c.followers.every((f) => usable(f.check))));

/**
 * The numbers the create dialog starts with, for whoever manages the node: each required port's default, or the next
 * number up whose port and followers are free (a second server of a game gets the next ports); optional ports off.
 */
export function suggestNumbers(
  ports: TemplatePort[],
  allocations: NodeAllocation[],
  inUse: PortInUse[] = [],
): Record<string, string> {
  const values: Record<string, string> = {};
  for (const port of chosenPorts(ports)) {
    if (!port.required) {
      values[port.name] = '';
      continue;
    }
    // A thousand numbers up is plenty: past that the node is full there, and the user types one.
    for (let number = port.default; number <= Math.min(65535, port.default + 1000); number++) {
      const tried = checkPorts(
        ports,
        { ...values, [port.name]: String(number) },
        allocations,
        true,
        undefined,
        inUse,
      ).find((c) => c.port.name === port.name);
      if (tried && usable(tried.check) && tried.followers.every((f) => usable(f.check))) {
        values[port.name] = String(number);
        break;
      }
    }
  }
  return values;
}

/**
 * The allocations the create dialog starts with, for whoever cannot add any: for each required port the free one on
 * its default, else the nearest above it, else the lowest, whose followers are free too; optional ports off.
 */
export function suggestAllocations(ports: TemplatePort[], allocations: NodeAllocation[]): Record<string, string> {
  const values: Record<string, string> = {};
  const taken = new Set<number>();
  for (const port of chosenPorts(ports)) {
    if (!port.required) {
      values[port.name] = '';
      continue;
    }
    const followers = followersOf(ports, port.name);
    const candidates = allocations
      .filter((a) => !a.inUse && !taken.has(a.port))
      .filter((a) =>
        followers.every((f) => {
          const next = a.port + (f.offset ?? 1);
          return (
            !taken.has(next) && allocations.some((o) => !o.inUse && o.ipAddress === a.ipAddress && o.port === next)
          );
        }),
      )
      .sort((a, b) => rank(a.port, port.default) - rank(b.port, port.default) || a.port - b.port);
    const chosen = candidates[0];
    if (chosen) {
      values[port.name] = chosen.id;
      taken.add(chosen.port);
      followers.forEach((f) => taken.add(chosen.port + (f.offset ?? 1)));
    } else {
      values[port.name] = '';
    }
  }
  return values;
}

/** The default first, then the ports above it, then those below. */
const rank = (port: number, preferred: number) => (port === preferred ? 0 : port > preferred ? 1 : 2);
