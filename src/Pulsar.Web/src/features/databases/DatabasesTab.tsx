import { Database } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import type { Schemas } from '../../api/client';
import { Button, Spinner } from '../../components/ui/Button';
import { RowActions } from '../../components/ui/RowActions';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Card, EmptyState, Table, Td, Th } from '../../components/ui/Display';
import { RefreshFailed } from '../../components/ui/RefreshFailed';
import { Field, Input, Select } from '../../components/ui/Form';
import { copyText } from '../../lib/clipboard';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { canOnServer, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import type { ServerDetails } from '../servers/api';
import {
  useCreateDatabase,
  useDatabasePassword,
  useDeleteDatabase,
  useRotateDatabasePassword,
  useServerDatabases,
  useSetGameDatabase,
  type ServerDatabase,
  type ServerDatabaseCredentials,
} from './api';

const MAX_NAME_LENGTH = 20;

/**
 * The server's MySQL/MariaDB and PostgreSQL databases (docs/architecture/07, "Databases"): created on a host the panel's
 * administrators set up, each with a user (a role, in PostgreSQL) of its own. Seeing them takes server.databases.view; creating, deleting,
 * rotating and reading passwords takes server.databases.manage.
 */
export function DatabasesTab({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const { server } = details;
  const databases = useServerDatabases(server.id);
  const reveal = useDatabasePassword(server.id);
  const rotate = useRotateDatabasePassword(server.id);
  const remove = useDeleteDatabase(server.id);
  const formatDate = useFormatDate();
  const [creating, setCreating] = useState(false);
  // The password on screen: the one just created, rotated or asked for (never kept in the query cache).
  const [shown, setShown] = useState<ServerDatabaseCredentials | null>(null);
  const canManage = canOnServer(me, server, Permission.ServerDatabasesManage);

  if (databases.isPending) {
    return <Spinner label={t('common.loading')} />;
  }
  // A failed refetch (after a reconnect, say) keeps the tab with the last data, and what is open on it.
  if (!databases.data) {
    return <Alert>{errorMessage(t, databases.error)}</Alert>;
  }

  const { limit, hosts, game } = databases.data;
  const list = databases.data.databases;
  const error = reveal.error ?? rotate.error ?? remove.error;
  // The game's database is not counted in the limit.
  const counted = list.filter((database) => !database.forGame).length;
  const full = counted >= limit;
  // The password stays only in `shown`: the mutations that brought it forget it.
  const rotatePassword = (database: ServerDatabase) =>
    window.confirm(t('databases.rotateConfirm', { name: database.name })) &&
    rotate.mutate(database.id, {
      onSuccess: (credentials) => {
        setShown(credentials);
        rotate.reset();
      },
    });
  const deleteDatabase = (database: ServerDatabase) =>
    window.confirm(t('databases.deleteConfirm', { name: database.name })) &&
    remove.mutate(database.id, {
      onSuccess: () => setShown((current) => (current?.database.id === database.id ? null : current)),
    });

  return (
    <div className="space-y-6">
      {databases.isError && <RefreshFailed error={databases.error} retry={() => void databases.refetch()} />}
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="max-w-2xl space-y-1 text-sm text-muted">
          <p>{t('databases.hint')}</p>
          <p>{t('databases.usage', { used: counted, count: limit })}</p>
        </div>
        {canManage && (
          <Button disabled={full || hosts.length === 0} onClick={() => setCreating(true)}>
            {t('databases.create')}
          </Button>
        )}
      </div>
      {game && (
        <GameDatabaseCard
          key={game.databaseId ?? 'own'}
          serverId={server.id}
          game={game}
          hosts={hosts}
          databases={list}
          canManage={canManage}
        />
      )}
      {canManage && hosts.length === 0 && <p className="text-sm text-muted">{t('databases.noHosts')}</p>}
      {canManage && full && hosts.length > 0 && <p className="text-sm text-muted">{t('databases.full')}</p>}
      {error && <Alert>{errorMessage(t, error)}</Alert>}
      {shown && <Credentials credentials={shown} onClose={() => setShown(null)} />}
      {list.length === 0 ? (
        <EmptyState icon={Database}>{t('databases.empty')}</EmptyState>
      ) : (
        <Table label={t('databases.title')}>
          <thead>
            <tr>
              <Th>{t('databases.name')}</Th>
              <Th>{t('databases.address')}</Th>
              <Th>{t('databases.created')}</Th>
              {canManage && (
                <Th>
                  <span className="sr-only">{t('common.actions')}</span>
                </Th>
              )}
            </tr>
          </thead>
          <tbody>
            {list.map((database) => (
              <tr key={database.id}>
                <Td>
                  <p className="font-mono">{database.name}</p>
                  <p className="text-xs text-muted">{t('databases.user', { user: database.userName })}</p>
                  {database.forGame && <p className="text-xs text-accent">{t('databases.forGame')}</p>}
                </Td>
                <Td>
                  <p className="font-mono">
                    {database.address}:{database.port}
                  </p>
                  {/* PostgreSQL says where its roles connect from in its own pg_hba.conf. */}
                  <p className="text-xs text-muted">
                    {database.engine === 'postgres'
                      ? t('databases.onPostgresHost', { host: database.host })
                      : t('databases.onHost', { host: database.host, from: database.userHost })}
                  </p>
                </Td>
                <Td>{formatDate(database.createdAt)}</Td>
                {canManage && (
                  <Td className="text-right whitespace-nowrap">
                    <RowActions name={database.name}>
                      <Button
                        variant="ghost"
                        size="sm"
                        disabled={reveal.isPending}
                        onClick={() =>
                          reveal.mutate(database.id, {
                            onSuccess: (credentials) => {
                              setShown(credentials);
                              reveal.reset();
                            },
                          })
                        }
                      >
                        {t('databases.showPassword')}
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        disabled={rotate.isPending}
                        onClick={() => rotatePassword(database)}
                      >
                        {t('databases.rotate')}
                      </Button>
                      {/* The game's data is there: it goes back to its own files first. */}
                      {!database.forGame && (
                        <Button
                          variant="ghost"
                          size="sm"
                          disabled={remove.isPending}
                          onClick={() => deleteDatabase(database)}
                        >
                          {t('actions.delete')}
                        </Button>
                      )}
                    </RowActions>
                  </Td>
                )}
              </tr>
            ))}
          </tbody>
        </Table>
      )}
      {creating && (
        <CreateDialog
          serverId={server.id}
          hosts={hosts}
          prefix={`s${server.shortId}_`}
          onCreated={(created) => {
            setCreating(false);
            setShown(created);
          }}
          onClose={() => setCreating(false)}
        />
      )}
    </div>
  );
}

/**
 * Where the game keeps its data (1.0.37): its own files, or a database of the server's on a host of the engine its
 * manifest takes. The change reaches the node at once and the game uses it from its next start.
 */
function GameDatabaseCard({
  serverId,
  game,
  hosts,
  databases,
  canManage,
}: {
  serverId: string;
  game: Schemas['GameDatabaseChoice'];
  hosts: Schemas['DatabaseHostChoice'][];
  databases: ServerDatabase[];
  canManage: boolean;
}) {
  const { t } = useTranslation();
  const set = useSetGameDatabase(serverId);
  const current = databases.find((database) => database.id === game.databaseId) ?? null;
  const choices = hosts.filter((host) => host.engine === game.engine);
  const [choice, setChoice] = useState(current?.hostId ?? '');
  const engine = t(`databaseHosts.engines.${game.engine}`);

  return (
    <Card className="space-y-3 p-5">
      <div>
        <h3 className="font-semibold">{t('databases.game.title')}</h3>
        <p className="mt-1 text-sm text-muted">
          {current
            ? t('databases.game.inDatabase', { name: current.name, host: current.host })
            : t('databases.game.ownFiles')}
        </p>
      </div>
      {canManage && (
        <form
          className="flex flex-wrap items-end gap-2"
          onSubmit={(event: FormEvent) => {
            event.preventDefault();
            set.mutate(choice === '' ? null : choice);
          }}
        >
          <Field label={t('databases.game.choose')}>
            {(control) => (
              <Select
                {...control}
                value={choice}
                disabled={!game.supported && current === null}
                onChange={(e) => {
                  set.reset();
                  setChoice(e.target.value);
                }}
              >
                <option value="">{t('databases.game.ownFilesOption')}</option>
                {choices.map((host) => (
                  <option key={host.id} value={host.id}>
                    {t('databases.game.hostOption', { engine, host: host.name })}
                  </option>
                ))}
              </Select>
            )}
          </Field>
          <Button type="submit" busy={set.isPending} disabled={choice === (current?.hostId ?? '')}>
            {t('actions.save')}
          </Button>
        </form>
      )}
      {canManage && !game.supported && <p className="text-xs text-muted">{t('databases.game.unsupported')}</p>}
      {canManage && choices.length === 0 && (
        <p className="text-xs text-muted">{t('databases.game.noHosts', { engine })}</p>
      )}
      {set.isError && <Alert>{errorMessage(t, set.error)}</Alert>}
      {set.isSuccess && (
        <p role="status" className="text-sm text-muted">
          {t('databases.game.saved')}
        </p>
      )}
      <p className="text-xs text-muted">{t('databases.game.hint')}</p>
    </Card>
  );
}

/** What a game's configuration needs, with the password: shown until closed. */
function Credentials({ credentials, onClose }: { credentials: ServerDatabaseCredentials; onClose: () => void }) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);
  const { database, password } = credentials;
  const items: [string, string][] = [
    [t('databases.address'), `${database.address}:${database.port}`],
    [t('databases.name'), database.name],
    [t('databases.userName'), database.userName],
    [t('databases.password'), password],
  ];
  return (
    <section
      aria-label={t('databases.credentials', { name: database.name })}
      className="rounded-xl border border-accent/40 bg-surface p-4"
    >
      <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-[max-content_1fr]">
        {items.map(([label, value]) => (
          <div key={label} className="contents">
            <dt className="text-muted">{label}</dt>
            <dd className="font-mono break-all">{value}</dd>
          </div>
        ))}
      </dl>
      <div className="mt-3 flex flex-wrap items-center justify-end gap-2">
        {copied && (
          <span role="status" className="text-xs text-success">
            {t('databases.copied')}
          </span>
        )}
        <Button variant="ghost" size="sm" onClick={async () => setCopied(await copyText(password))}>
          {t('databases.copyPassword')}
        </Button>
        <Button variant="ghost" size="sm" onClick={onClose}>
          {t('actions.close')}
        </Button>
      </div>
    </section>
  );
}

function CreateDialog({
  serverId,
  hosts,
  prefix,
  onCreated,
  onClose,
}: {
  serverId: string;
  hosts: Schemas['DatabaseHostChoice'][];
  prefix: string;
  onCreated: (created: ServerDatabaseCredentials) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const create = useCreateDatabase(serverId);
  const [hostId, setHostId] = useState(hosts[0]?.id ?? '');
  const [name, setName] = useState('');
  const valid = /^[a-z0-9_]{1,20}$/.test(name);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (valid) {
      create.mutate({ hostId, name }, { onSuccess: onCreated });
    }
  };

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()} title={t('databases.create')}>
      <form className="space-y-4" onSubmit={submit} noValidate>
        {hosts.length > 1 && (
          <Field label={t('databases.host')}>
            {(control) => (
              <Select {...control} value={hostId} onChange={(e) => setHostId(e.target.value)}>
                {hosts.map((host) => (
                  <option key={host.id} value={host.id}>
                    {host.engine === 'postgres' ? t('databases.postgresHostChoice', { host: host.name }) : host.name}
                  </option>
                ))}
              </Select>
            )}
          </Field>
        )}
        <Field
          label={t('databases.name')}
          hint={t('databases.nameHint', { name: `${prefix}${name || '…'}` })}
          error={name !== '' && !valid ? t('databases.nameInvalid') : undefined}
        >
          {(control) => (
            <Input
              {...control}
              autoComplete="off"
              maxLength={MAX_NAME_LENGTH}
              value={name}
              onChange={(e) => setName(e.target.value.toLowerCase())}
            />
          )}
        </Field>
        {create.error && <Alert>{errorMessage(t, create.error)}</Alert>}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button type="submit" busy={create.isPending} disabled={!valid || hostId === ''}>
            {t('actions.create')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
