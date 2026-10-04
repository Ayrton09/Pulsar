import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Badge, Card, Table, Td, Th } from '../../components/ui/Display';
import { Field, Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDate, useFormatFileSize } from '../../lib/format';
import { can, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import type { ServerDetails } from '../servers/api';
import {
  backupDownloadUrl,
  useBackupLimit,
  useBackupStorageChoice,
  useBackups,
  useCreateBackup,
  useDeleteBackup,
  useLockBackup,
  useRestoreBackup,
  type BackupView,
} from './api';

const MAX_NAME_LENGTH = 128;
const MAX_LIMIT = 1000;

const statusTones = {
  creating: 'accent',
  completed: 'success',
  failed: 'danger',
  cancelled: 'neutral',
} as const;

/**
 * The server's backups (docs/architecture/07, "Backups"): zips its node keeps, made while the server runs if need be,
 * restored with the server stopped. Kept fresh by the live channel (a backup completing changes the server).
 */
export function BackupsTab({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const { server } = details;
  const backups = useBackups(server.id);
  const restore = useRestoreBackup(server.id);
  const lock = useLockBackup(server.id);
  const remove = useDeleteBackup(server.id);
  const [creating, setCreating] = useState(false);
  const canCreate = can(me, Permission.ServerBackupsCreate);
  const canRestore = can(me, Permission.ServerBackupsRestore);
  const canDelete = can(me, Permission.ServerBackupsDelete);
  const connected = server.nodeConnected;
  const idle = server.activeOperation === null;
  const stopped = server.runtimeState === 'offline' || server.runtimeState === 'crashed';
  const installed = server.installState === 'installed';

  if (backups.isPending) {
    return <Spinner label={t('common.loading')} />;
  }
  if (backups.isError) {
    return <Alert>{errorMessage(t, backups.error)}</Alert>;
  }

  const error = restore.error ?? lock.error ?? remove.error;
  const restoreBackup = (backup: BackupView) =>
    window.confirm(t('backups.restoreConfirm', { name: backup.name, server: server.name })) &&
    restore.mutate(backup.id);
  const deleteBackup = (backup: BackupView) =>
    window.confirm(t('backups.deleteConfirm', { name: backup.name })) && remove.mutate(backup.id);

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <p className="max-w-2xl text-sm text-muted">{t('backups.hint')}</p>
        {canCreate && (
          <Button disabled={!connected || !idle || !installed} onClick={() => setCreating(true)}>
            {t('backups.create')}
          </Button>
        )}
      </div>
      {canRestore && !stopped && <p className="text-sm text-muted">{t('backups.stopToRestore')}</p>}
      {error && <Alert>{errorMessage(t, error)}</Alert>}

      {backups.data.backups.length === 0 ? (
        <p className="text-sm text-muted">{t('backups.empty')}</p>
      ) : (
        <Table label={t('backups.title')}>
          <thead>
            <tr>
              <Th>{t('backups.name')}</Th>
              <Th className="hidden md:table-cell">{t('backups.created')}</Th>
              <Th className="hidden text-right sm:table-cell">{t('backups.size')}</Th>
              <Th>{t('backups.status')}</Th>
              <Th className="text-right">
                <span className="sr-only">{t('backups.actions')}</span>
              </Th>
            </tr>
          </thead>
          <tbody>
            {backups.data.backups.map((backup) => (
              <BackupRow
                key={backup.id}
                serverId={server.id}
                backup={backup}
                canRestore={canRestore && connected && idle && stopped}
                showRestore={canRestore}
                canDelete={canDelete && connected}
                showDelete={canDelete}
                pending={
                  (restore.isPending && restore.variables === backup.id) ||
                  (lock.isPending && lock.variables.backupId === backup.id) ||
                  (remove.isPending && remove.variables === backup.id)
                }
                onRestore={() => restoreBackup(backup)}
                onLock={(locked) => lock.mutate({ backupId: backup.id, locked })}
                onDelete={() => deleteBackup(backup)}
              />
            ))}
          </tbody>
        </Table>
      )}

      {canDelete && <RetentionCard serverId={server.id} limit={backups.data.limit} />}
      {canDelete && (
        <RemoteCopyCard serverId={server.id} storageId={backups.data.storageId} storages={backups.data.storages} />
      )}
      {creating && <CreateBackupDialog serverId={server.id} running={!stopped} onDone={() => setCreating(false)} />}
    </div>
  );
}

interface BackupRowProps {
  serverId: string;
  backup: BackupView;
  canRestore: boolean;
  showRestore: boolean;
  canDelete: boolean;
  showDelete: boolean;
  pending: boolean;
  onRestore: () => void;
  onLock: (locked: boolean) => void;
  onDelete: () => void;
}

function BackupRow({
  serverId,
  backup,
  canRestore,
  showRestore,
  canDelete,
  showDelete,
  pending,
  onRestore,
  onLock,
  onDelete,
}: BackupRowProps) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const formatSize = useFormatFileSize();
  const completed = backup.status === 'completed';
  return (
    <tr>
      <Td className="max-w-md">
        <span className="flex min-w-0 flex-wrap items-center gap-2">
          <span className="truncate font-medium">{backup.name}</span>
          {backup.locked && <Badge>{t('backups.locked')}</Badge>}
          {backup.createdVia !== 'user' && <Badge>{t(`backups.via.${backup.createdVia}`)}</Badge>}
        </span>
        <p className="mt-1 text-xs text-muted md:hidden">{formatDate(backup.createdAt)}</p>
        {completed && (
          <p className="mt-1 text-xs text-muted">
            {t('backups.facts', {
              count: backup.fileCount ?? 0,
              seconds: Math.max(1, Math.round(backup.durationSeconds ?? 0)),
            })}
            {(backup.skippedFiles ?? 0) > 0 && ` · ${t('backups.skipped', { count: backup.skippedFiles ?? 0 })}`}
          </p>
        )}
        {backup.status === 'failed' && backup.errorMessage && (
          <p className="mt-1 text-xs break-words text-danger">{backup.errorMessage}</p>
        )}
        {backup.remoteStatus !== 'none' && (
          <p className={`mt-1 text-xs ${backup.remoteStatus === 'failed' ? 'text-danger' : 'text-muted'}`}>
            {t(`backups.remote.statuses.${backup.remoteStatus}`)}
            {backup.remoteError && `: ${backup.remoteError}`}
          </p>
        )}
      </Td>
      <Td className="hidden whitespace-nowrap text-muted md:table-cell">{formatDate(backup.createdAt)}</Td>
      <Td className="hidden text-right whitespace-nowrap text-muted tabular-nums sm:table-cell">
        {backup.size === null ? '' : formatSize(backup.size)}
      </Td>
      <Td>
        <Badge tone={statusTones[backup.status]}>{t(`backups.statuses.${backup.status}`)}</Badge>
      </Td>
      <Td className="text-right whitespace-nowrap">
        <span className="inline-flex flex-wrap justify-end gap-1">
          {completed && (
            <a
              href={backupDownloadUrl(serverId, backup.id)}
              className="inline-flex h-8 items-center rounded-md px-3 text-sm font-medium text-accent hover:bg-surface-raised"
              aria-label={t('backups.downloadNamed', { name: backup.name })}
            >
              {t('backups.download')}
            </a>
          )}
          {completed && showRestore && (
            <Button
              variant="ghost"
              size="sm"
              disabled={!canRestore || pending}
              aria-label={t('backups.restoreNamed', { name: backup.name })}
              onClick={onRestore}
            >
              {t('backups.restore')}
            </Button>
          )}
          {completed && showDelete && (
            <Button
              variant="ghost"
              size="sm"
              disabled={pending}
              aria-label={t(backup.locked ? 'backups.unlockNamed' : 'backups.lockNamed', { name: backup.name })}
              onClick={() => onLock(!backup.locked)}
            >
              {t(backup.locked ? 'backups.unlock' : 'backups.lock')}
            </Button>
          )}
          {showDelete && backup.status !== 'creating' && (
            <Button
              variant="ghost"
              size="sm"
              disabled={!canDelete || backup.locked || pending}
              aria-label={t('backups.deleteNamed', { name: backup.name })}
              onClick={onDelete}
            >
              {t('actions.delete')}
            </Button>
          )}
        </span>
      </Td>
    </tr>
  );
}

function CreateBackupDialog({ serverId, running, onDone }: { serverId: string; running: boolean; onDone: () => void }) {
  const { t } = useTranslation();
  const create = useCreateBackup(serverId);
  const [name, setName] = useState('');
  const tooLong = name.trim().length > MAX_NAME_LENGTH;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!tooLong) {
      create.mutate(name.trim() === '' ? null : name.trim(), { onSuccess: onDone });
    }
  };

  return (
    <Dialog open onOpenChange={(open) => !open && onDone()} title={t('backups.createTitle')}>
      <form onSubmit={submit} className="space-y-4" noValidate>
        <Field
          label={t('backups.name')}
          hint={t('backups.nameHint')}
          error={tooLong ? t('backups.nameTooLong', { max: MAX_NAME_LENGTH }) : undefined}
        >
          {(control) => (
            <Input
              {...control}
              value={name}
              maxLength={MAX_NAME_LENGTH + 1}
              onChange={(e) => setName(e.target.value)}
            />
          )}
        </Field>
        {running && <p className="text-sm text-muted">{t('backups.runningWarning')}</p>}
        {create.isError && <Alert>{errorMessage(t, create.error)}</Alert>}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onDone}>
            {t('actions.cancel')}
          </Button>
          <Button type="submit" busy={create.isPending} disabled={tooLong}>
            {t('backups.create')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}

/** How many backups to keep; the oldest unlocked ones beyond it are deleted after each new backup. */
function RetentionCard({ serverId, limit }: { serverId: string; limit: number | null }) {
  const { t } = useTranslation();
  const save = useBackupLimit(serverId);
  const [value, setValue] = useState(limit === null ? '' : String(limit));
  const parsed = value.trim() === '' ? null : Number(value);
  const invalid = parsed !== null && (!Number.isInteger(parsed) || parsed < 1 || parsed > MAX_LIMIT);
  const changed = parsed !== limit;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!invalid && changed) {
      const lowering = parsed !== null && (limit === null || parsed < limit);
      if (!lowering || window.confirm(t('backups.retention.confirm', { count: parsed }))) {
        save.mutate(parsed);
      }
    }
  };

  return (
    <Card className="p-6">
      <h2 className="text-lg font-semibold">{t('backups.retention.title')}</h2>
      <p className="mt-1 text-sm text-muted">{t('backups.retention.hint')}</p>
      <form onSubmit={submit} className="mt-4 flex flex-wrap items-end gap-3" noValidate>
        <div className="w-48">
          <Field
            label={t('backups.retention.keep')}
            error={invalid ? t('backups.retention.invalid', { max: MAX_LIMIT }) : undefined}
          >
            {(control) => (
              <Input
                {...control}
                type="number"
                inputMode="numeric"
                min={1}
                max={MAX_LIMIT}
                placeholder={t('backups.retention.all')}
                value={value}
                onChange={(e) => setValue(e.target.value)}
              />
            )}
          </Field>
        </div>
        <Button type="submit" variant="secondary" busy={save.isPending} disabled={invalid || !changed}>
          {t('actions.save')}
        </Button>
      </form>
      {save.isError && (
        <div className="mt-3">
          <Alert>{errorMessage(t, save.error)}</Alert>
        </div>
      )}
      {save.isSuccess && !changed && (
        <p role="status" className="mt-3 text-sm text-success">
          {t('backups.retention.saved')}
        </p>
      )}
    </Card>
  );
}

/** Where new backups are copied besides the node: one of the panel's backup storages, or none. */
function RemoteCopyCard({
  serverId,
  storageId,
  storages,
}: {
  serverId: string;
  storageId: string | null;
  storages: { id: string; name: string }[];
}) {
  const { t } = useTranslation();
  const save = useBackupStorageChoice(serverId);
  const [value, setValue] = useState(storageId ?? '');
  const changed = (value || null) !== storageId;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (changed) {
      save.mutate(value || null);
    }
  };

  return (
    <Card className="p-6">
      <h2 className="text-lg font-semibold">{t('backups.remote.title')}</h2>
      <p className="mt-1 text-sm text-muted">{t('backups.remote.hint')}</p>
      {storages.length === 0 && storageId === null ? (
        <p className="mt-4 text-sm text-muted">{t('backups.remote.none')}</p>
      ) : (
        <form onSubmit={submit} className="mt-4 flex flex-wrap items-end gap-3" noValidate>
          <div className="w-72">
            <Field label={t('backups.remote.storage')}>
              {(control) => (
                <Select {...control} value={value} onChange={(e) => setValue(e.target.value)}>
                  <option value="">{t('backups.remote.nodeOnly')}</option>
                  {storages.map((storage) => (
                    <option key={storage.id} value={storage.id}>
                      {storage.name}
                    </option>
                  ))}
                </Select>
              )}
            </Field>
          </div>
          <Button type="submit" variant="secondary" busy={save.isPending} disabled={!changed}>
            {t('actions.save')}
          </Button>
        </form>
      )}
      {save.isError && (
        <div className="mt-3">
          <Alert>{errorMessage(t, save.error)}</Alert>
        </div>
      )}
      {save.isSuccess && !changed && (
        <p role="status" className="mt-3 text-sm text-success">
          {t('backups.remote.saved')}
        </p>
      )}
    </Card>
  );
}
