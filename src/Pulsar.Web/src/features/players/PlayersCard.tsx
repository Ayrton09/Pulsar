import { Ban, LogOut, ShieldCheck, UserCheck, X, type LucideIcon } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { Alert, Card, Table, Td, Th } from '../../components/ui/Display';
import { Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDuration } from '../../lib/format';
import { canOnServer, Permission } from '../../lib/permissions';
import { visibleName } from '../../lib/visibleText';
import { useCurrentUser } from '../auth/api';
import type { ServerQuery } from '../live/types';
import type { ServerDetails } from '../servers/api';
import { commandRefusal } from '../servers/serverStatus';
import {
  listedName,
  listRemoval,
  needsConsole,
  playerName,
  usePlayerAction,
  usePlayerLists,
  type PlayerAction,
  type PlayerListView,
  type ServerPlayersView,
} from './api';

/** What the buttons next to a player who plays do, in this order. */
const rowActions: { action: PlayerAction; icon: LucideIcon; confirm: boolean }[] = [
  { action: 'kick', icon: LogOut, confirm: false },
  { action: 'ban', icon: Ban, confirm: true },
  { action: 'whitelistAdd', icon: UserCheck, confirm: false },
  { action: 'adminAdd', icon: ShieldCheck, confirm: true },
];

/**
 * Who is playing, as the game answered its query (names are the players' own, shown as text), and what the panel can do
 * to a player with the game's own commands: kick, ban, whitelist, make admin, by the list or by name, and the game's
 * lists of who is banned, whitelisted or admin.
 */
export function PlayersCard({ details, query }: { details: ServerDetails; query: ServerQuery | null | undefined }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const { server } = details;
  const players = details.players ?? null;
  const canManage = players !== null && canOnServer(me, server, Permission.ServerPlayersManage);
  const canConsole = canOnServer(me, server, Permission.ServerConsoleWrite);
  const running = server.runtimeState === 'starting' || server.runtimeState === 'running';
  const ready = running && server.nodeConnected;
  const actions = canManage ? players.actions.filter((action) => !needsConsole(action) || canConsole) : [];
  const act = usePlayerAction(server.id);

  const send = (action: PlayerAction, player: string) => act.mutate({ action, player });
  const refused = act.data && act.data.status !== 'accepted';

  return (
    <Card className="p-6">
      <h2 className="text-lg font-semibold">{t('servers.players.title')}</h2>
      {query && (query.name || query.version) && (
        <p className="mt-1 text-sm break-words text-muted">
          {[query.name, query.version && t('servers.players.version', { version: query.version })]
            .filter(Boolean)
            .join(' · ')}
        </p>
      )}
      {running && query && (
        <div className="mt-4">
          <Online
            query={query}
            players={players}
            actions={ready && players?.byId === false ? actions : []}
            busy={act.isPending}
            onAct={(action, name) => {
              if (
                !rowActions.find((r) => r.action === action)?.confirm ||
                window.confirm(t(`players.confirm.${action}`, { name }))
              ) {
                send(action, name);
              }
            }}
          />
        </div>
      )}
      {canManage && actions.length > 0 && (
        <ByName players={players} actions={actions} ready={ready} busy={act.isPending} onAct={send} />
      )}
      {act.isError && (
        <div className="mt-3">
          <Alert>{errorMessage(t, act.error)}</Alert>
        </div>
      )}
      {act.isSuccess && !refused && (
        <p role="status" className="mt-3 text-sm text-success">
          {t('players.sent')}
        </p>
      )}
      {refused && (
        <p role="status" className="mt-3 text-sm text-danger">
          {commandRefusal(t, act.data.status, act.data.errorCode)}
        </p>
      )}
      {canManage && players.lists.length > 0 && (
        <Lists
          serverId={server.id}
          players={players}
          actions={actions}
          ready={ready}
          busy={act.isPending}
          onAct={(action, name) => {
            if (window.confirm(t(`players.confirm.${action}`, { name }))) {
              send(action, name);
            }
          }}
        />
      )}
    </Card>
  );
}

function Online({
  query,
  players,
  actions,
  busy,
  onAct,
}: {
  query: ServerQuery;
  players: ServerPlayersView | null;
  actions: PlayerAction[];
  busy: boolean;
  onAct: (action: PlayerAction, name: string) => void;
}) {
  const { t } = useTranslation();
  const formatDuration = useFormatDuration();
  const buttons = rowActions.filter((r) => actions.includes(r.action));
  if (query.playerList.length === 0) {
    return (
      <p className="text-sm text-muted">
        {query.players > 0 ? t('servers.players.notListed') : t('servers.players.empty')}
      </p>
    );
  }
  return (
    <Table label={t('servers.players.title')}>
      <thead>
        <tr>
          <Th>{t('servers.players.name')}</Th>
          <Th>{t('servers.players.score')}</Th>
          <Th>{t('servers.players.time')}</Th>
          {buttons.length > 0 && <Th className="text-right">{t('players.actions')}</Th>}
        </tr>
      </thead>
      <tbody>
        {query.playerList.map((player, index) => {
          // A name the game's console could not take (quotes, symbols) gets no buttons: the panel never sends another name.
          const name = listedName(player.name, players?.spaces ?? false);
          return (
            <tr key={index}>
              <Td className="break-all">{visibleName(player.name)}</Td>
              <Td className="tabular-nums">{player.score ?? '—'}</Td>
              <Td className="tabular-nums">
                {player.connectedSeconds === null ? '—' : formatDuration(player.connectedSeconds)}
              </Td>
              {buttons.length > 0 && (
                <Td className="text-right whitespace-nowrap">
                  {name === null ? (
                    <span className="text-xs text-muted" title={t('players.nameUnusable')}>
                      —
                    </span>
                  ) : (
                    buttons.map(({ action, icon }) => (
                      <Button
                        key={action}
                        variant="ghost"
                        size="icon"
                        icon={icon}
                        aria-label={t(`players.rowActions.${action}`, { name })}
                        title={t(`players.rowActions.${action}`, { name })}
                        disabled={busy}
                        onClick={() => onAct(action, name)}
                      />
                    ))
                  )}
                </Td>
              )}
            </tr>
          );
        })}
      </tbody>
    </Table>
  );
}

/** Any player, playing or not: an unban, a whitelist made before they join. */
function ByName({
  players,
  actions,
  ready,
  busy,
  onAct,
}: {
  players: ServerPlayersView;
  actions: PlayerAction[];
  ready: boolean;
  busy: boolean;
  onAct: (action: PlayerAction, player: string) => void;
}) {
  const { t } = useTranslation();
  const [player, setPlayer] = useState('');
  const [action, setAction] = useState<PlayerAction>(actions[0] ?? 'kick');
  const chosen = actions.includes(action) ? action : (actions[0] ?? 'kick');
  const name = playerName(player, players.spaces);
  const label = players.byId ? t('players.byName.id') : t('players.byName.name');

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (name !== null && ready) {
      onAct(chosen, name);
    }
  };

  return (
    <form onSubmit={submit} className="mt-6 space-y-2 border-t border-border pt-4">
      <h3 className="text-sm font-semibold">{t('players.byName.title')}</h3>
      <div className="flex flex-wrap gap-2">
        <Input
          aria-label={label}
          placeholder={label}
          className="min-w-40 flex-1"
          maxLength={64}
          autoComplete="off"
          spellCheck={false}
          value={player}
          aria-invalid={player.trim() !== '' && name === null ? true : undefined}
          onChange={(e) => setPlayer(e.target.value)}
        />
        <Select
          aria-label={t('players.byName.action')}
          className="w-auto"
          value={chosen}
          onChange={(e) => setAction(e.target.value as PlayerAction)}
        >
          {actions.map((a) => (
            <option key={a} value={a}>
              {t(`players.names.${a}`)}
            </option>
          ))}
        </Select>
        <Button type="submit" disabled={!ready || name === null} busy={busy}>
          {t('players.byName.send')}
        </Button>
      </div>
      <p className="text-xs text-muted">
        {!ready
          ? t('players.notRunning')
          : player.trim() !== '' && name === null
            ? t(players.spaces ? 'players.invalidSpaces' : 'players.invalid')
            : t('players.byName.hint')}
      </p>
    </form>
  );
}

function Lists({
  serverId,
  players,
  actions,
  ready,
  busy,
  onAct,
}: {
  serverId: string;
  players: ServerPlayersView;
  actions: PlayerAction[];
  ready: boolean;
  busy: boolean;
  onAct: (action: PlayerAction, player: string) => void;
}) {
  const { t } = useTranslation();
  const lists = usePlayerLists(serverId, true);
  return (
    <section className="mt-6 space-y-4 border-t border-border pt-4" aria-label={t('players.lists.title')}>
      <h3 className="text-sm font-semibold">{t('players.lists.title')}</h3>
      {lists.isError && <Alert>{errorMessage(t, lists.error)}</Alert>}
      {players.lists.map((kind) => {
        const list = lists.data?.lists.find((l) => l.kind === kind);
        const removal = listRemoval[kind];
        return (
          <PlayerList
            key={kind}
            kind={kind}
            list={list}
            loading={lists.isPending}
            removal={ready && actions.includes(removal) ? removal : null}
            spaces={players.spaces}
            busy={busy}
            onRemove={(name) => onAct(removal, name)}
          />
        );
      })}
    </section>
  );
}

function PlayerList({
  kind,
  list,
  loading,
  removal,
  spaces,
  busy,
  onRemove,
}: {
  kind: PlayerListView['kind'];
  list: PlayerListView | undefined;
  loading: boolean;
  removal: PlayerAction | null;
  spaces: boolean;
  busy: boolean;
  onRemove: (name: string) => void;
}) {
  const { t } = useTranslation();
  return (
    <div>
      <p className="text-sm text-muted">
        {t(`players.lists.${kind}`)}
        {list && list.players.length > 0 && ` (${list.players.length}${list.truncated ? '+' : ''})`}
      </p>
      {loading ? (
        <p className="mt-1 text-sm text-muted">{t('common.loading')}</p>
      ) : list?.errorCode ? (
        <p className="mt-1 text-sm text-danger">{t(`errors.${list.errorCode}`)}</p>
      ) : !list || list.players.length === 0 ? (
        <p className="mt-1 text-sm text-muted">{t('players.lists.empty')}</p>
      ) : (
        <ul className="mt-2 flex flex-wrap gap-2">
          {list.players.map((player) => {
            const name = listedName(player, spaces);
            return (
              <li
                key={player}
                className="inline-flex max-w-full items-center gap-1 rounded-md border border-border bg-surface-raised py-0.5 pr-0.5 pl-2 text-sm"
              >
                <span className="break-all">{visibleName(player)}</span>
                {removal && name !== null && (
                  <Button
                    variant="ghost"
                    size="icon"
                    icon={X}
                    className="size-6"
                    aria-label={t(`players.rowActions.${removal}`, { name })}
                    title={t(`players.rowActions.${removal}`, { name })}
                    disabled={busy}
                    onClick={() => onRemove(name)}
                  />
                )}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
