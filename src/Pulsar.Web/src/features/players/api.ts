import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { adminKeys } from '../admin/api';

export type PlayerAction = Schemas['PlayerAction'];
export type PlayerListKind = Schemas['PlayerListKind'];
export type PlayerListView = Schemas['PlayerListView'];
export type ServerPlayersView = Schemas['ServerPlayersView'];

/**
 * Not under the server's keys: the live channel refreshes those on every change of the server, and each read of the
 * lists is a file read on its node.
 */
export const playerKeys = {
  lists: (serverId: string) => ['playerLists', serverId] as const,
};

/** How long the game takes to write its list once a command changed it, give or take. */
const LIST_WRITTEN_MS = 2000;

/** Who is banned, whitelisted or admin, as the game's files say (read on the node). */
export function usePlayerLists(serverId: string, enabled: boolean) {
  return useQuery({
    queryKey: playerKeys.lists(serverId),
    queryFn: () => unwrap(api.GET('/api/v1/servers/{id}/players/lists', { params: { path: { id: serverId } } })),
    enabled,
    staleTime: 10_000,
  });
}

/** The game's command for one player, through its console; audited. The lists are read again once the game wrote them. */
export function usePlayerAction(serverId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: { action: PlayerAction; player: string }) =>
      unwrap(api.POST('/api/v1/servers/{id}/players', { params: { path: { id: serverId } }, body })),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: adminKeys.audit });
      window.setTimeout(
        () => void queryClient.invalidateQueries({ queryKey: playerKeys.lists(serverId) }),
        LIST_WRITTEN_MS,
      );
    },
  });
}

/**
 * The name as the panel sends it (the API's GameCommands.PlayerName): 1 to 64 letters, digits and _ - . not starting
 * with - (and single spaces when the game takes them), without the spaces around it; null for anything else. Only to
 * offer what the panel would take: the API checks it again.
 */
export function playerName(name: string, spaces: boolean): string | null {
  const trimmed = name.trim();
  if (trimmed.length === 0 || trimmed.length > 64 || trimmed.startsWith('-')) {
    return null;
  }
  const pattern = spaces ? /^[\p{L}\p{Nd}_.-]+( [\p{L}\p{Nd}_.-]+)*$/u : /^[\p{L}\p{Nd}_.-]+$/u;
  // A letter outside the BMP is two UTF-16 units the API does not take.
  return pattern.test(trimmed) && !/[\u{10000}-\u{10FFFF}]/u.test(trimmed) ? trimmed : null;
}

/**
 * A name the game listed (its query, its files) that the panel sends exactly as it is: one with spaces around it is
 * not, since the panel would send it without them, which may be another player's name.
 */
export function listedName(name: string, spaces: boolean): string | null {
  return playerName(name, spaces) === name ? name : null;
}

/** What takes a player off each list. */
export const listRemoval: Record<PlayerListKind, PlayerAction> = {
  banned: 'unban',
  whitelist: 'whitelistRemove',
  admins: 'adminRemove',
};

/** Making a player the game's admin, or taking it away, also needs the right to send console commands. */
export function needsConsole(action: PlayerAction): boolean {
  return action === 'adminAdd' || action === 'adminRemove';
}
