import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Table, Td, Th } from '../../components/ui/Display';
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
  type ServerDatabase,
  type ServerDatabaseCredentials,
} from './api';

const MAX_NAME_LENGTH = 20;

/**
 * The server's MySQL/MariaDB databases (docs/architecture/07, "Databases"): created on a host the panel's
 * administrators set up, each with a user of its own. Seeing them takes server.databases.view; creating, deleting,
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
  if (databases.isError) {
    return <Alert>{errorMessage(t, databases.error)}</Alert>;
  }

  const { limit, hosts } = databases.data;
  const list = databases.data.databases;
  const error = reveal.error ?? rotate.error ?? remove.error;
  const full = list.length >= limit;
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
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="max-w-2xl space-y-1 text-sm text-muted">
          <p>{t('databases.hint')}</p>
          <p>{t('databases.usage', { used: list.length, count: limit })}</p>
        </div>
        {canManage && (
          <Button disabled={full || hosts.length === 0} onClick={() => setCreating(true)}>
            {t('databases.create')}
          </Button>
        )}
      </div>
      {canManage && hosts.length === 0 && <p className="text-sm text-muted">{t('databases.noHosts')}</p>}
      {canManage && full && hosts.length > 0 && <p className="text-sm text-muted">{t('databases.full')}</p>}
      {error && <Alert>{errorMessage(t, error)}</Alert>}
      {shown && <Credentials credentials={shown} onClose={() => setShown(null)} />}
      {list.length === 0 ? (
        <p className="text-sm text-muted">{t('databases.empty')}</p>
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
                </Td>
                <Td>
                  <p className="font-mono">
                    {database.address}:{database.port}
                  </p>
                  <p className="text-xs text-muted">
                    {t('databases.onHost', { host: database.host, from: database.userHost })}
                  </p>
                </Td>
                <Td>{formatDate(database.createdAt)}</Td>
                {canManage && (
                  <Td className="text-right whitespace-nowrap">
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
                    <Button
                      variant="ghost"
                      size="sm"
                      disabled={remove.isPending}
                      onClick={() => deleteDatabase(database)}
                    >
                      {t('actions.delete')}
                    </Button>
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
  hosts: { id: string; name: string }[];
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
                    {host.name}
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
