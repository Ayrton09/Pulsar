import { useId, useState, type FormEvent, type InputHTMLAttributes } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { RowActions } from '../../components/ui/RowActions';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Badge, Card, Table, Td, Th } from '../../components/ui/Display';
import { RefreshFailed } from '../../components/ui/RefreshFailed';
import { Checkbox, Field, Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDate, useFormatFileSize } from '../../lib/format';
import { canOnServer, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import type { ServerDetails } from '../servers/api';
import { failureReason } from '../servers/serverStatus';
import { breadcrumbs, joinPath, visibleName } from '../files/paths';
import {
  backupDownloadUrl,
  useBackupLimit,
  useBackupStorageChoice,
  useBackupFiles,
  useBackups,
  useCreateBackup,
  useDeleteBackup,
  useLockBackup,
  useRestoreBackup,
  useRestoreBackupFiles,
  type BackupView,
} from './api';

const MAX_NAME_LENGTH = 128;
const MAX_LIMIT = 1000;
/** Most files and folders one restore picks (the panel's limit). */
const MAX_PICKED = 100;

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
  // Here, not in their cards: a save remounts a card with what the panel stored, and the result must survive that.
  const saveLimit = useBackupLimit(server.id);
  const saveStorage = useBackupStorageChoice(server.id);
  const [creating, setCreating] = useState(false);
  const [restoring, setRestoring] = useState<BackupView | null>(null);
  const canCreate = canOnServer(me, server, Permission.ServerBackupsCreate);
  const canRestore = canOnServer(me, server, Permission.ServerBackupsRestore);
  const canDelete = canOnServer(me, server, Permission.ServerBackupsDelete);
  const connected = server.nodeConnected;
  const idle = server.activeOperation === null;
  const stopped = server.runtimeState === 'offline' || server.runtimeState === 'crashed';
  const installed = server.installState === 'installed';

  if (backups.isPending) {
    return <Spinner label={t('common.loading')} />;
  }
  // A failed refetch (after a reconnect, say) keeps the tab with the last data, and what is open on it.
  if (!backups.data) {
    return <Alert>{errorMessage(t, backups.error)}</Alert>;
  }

  const error = lock.error ?? remove.error;
  const deleteBackup = (backup: BackupView) =>
    window.confirm(t('backups.deleteConfirm', { name: backup.name })) && remove.mutate(backup.id);

  return (
    <div className="space-y-6">
      {backups.isError && <RefreshFailed error={backups.error} retry={() => void backups.refetch()} />}
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
                  (restore.isPending && restore.variables.backupId === backup.id) ||
                  (lock.isPending && lock.variables.backupId === backup.id) ||
                  (remove.isPending && remove.variables === backup.id)
                }
                onRestore={() => {
                  restore.reset();
                  setRestoring(backup);
                }}
                onLock={(locked) => lock.mutate({ backupId: backup.id, locked })}
                onDelete={() => deleteBackup(backup)}
              />
            ))}
          </tbody>
        </Table>
      )}

      {/* Keyed on what is stored: a change made elsewhere (another admin) starts the field again from it. */}
      {canDelete && <RetentionCard key={backups.data.limit ?? 'all'} limit={backups.data.limit} save={saveLimit} />}
      {canDelete && (
        <RemoteCopyCard
          key={backups.data.storageId ?? 'node'}
          storageId={backups.data.storageId}
          storages={backups.data.storages}
          save={saveStorage}
        />
      )}
      {creating && <CreateBackupDialog serverId={server.id} running={!stopped} onDone={() => setCreating(false)} />}
      {restoring && (
        <RestoreBackupDialog
          serverId={server.id}
          backup={restoring}
          serverName={server.name}
          restore={restore}
          onDone={() => setRestoring(null)}
        />
      )}
    </div>
  );
}

/**
 * How the node read the files: from a snapshot, or live while the game ran (and why), and whether the game saved
 * first. Nothing for a backup of a stopped server, or one made before Pulsar said.
 */
function CopyNote({ backup }: { backup: BackupView }) {
  const { t } = useTranslation();
  if (backup.copyMode !== 'snapshot' && backup.copyMode !== 'live') {
    return null;
  }

  const reason = backup.snapshotError?.startsWith('snapshot.')
    ? backup.snapshotError.slice('snapshot.'.length)
    : 'unknown';
  const copy =
    backup.copyMode === 'snapshot'
      ? t('backups.copy.snapshot')
      : t('backups.copy.live', {
          reason: t(`backups.snapshotErrors.${reason}`, { defaultValue: t('backups.snapshotErrors.unknown') }),
        });
  return (
    <p className="mt-1 text-xs text-muted">
      {copy}
      {backup.gameSave && ` · ${t(`backups.save.${backup.gameSave}`)}`}
    </p>
  );
}

function RestoreBackupDialog({
  serverId,
  backup,
  serverName,
  restore,
  onDone,
}: {
  serverId: string;
  backup: BackupView;
  serverName: string;
  restore: ReturnType<typeof useRestoreBackup>;
  onDone: () => void;
}) {
  const { t } = useTranslation();
  const restoreFiles = useRestoreBackupFiles(serverId);
  const [clean, setClean] = useState(false);
  const [some, setSome] = useState(false);
  const [picked, setPicked] = useState<string[]>([]);
  const hintId = useId();
  // A clean restore would also delete the files this backup left out because the game held them open.
  const incomplete = (backup.skippedFiles ?? 0) > 0;
  const pending = restore.isPending || restoreFiles.isPending;
  const failed = some ? restoreFiles.error : restore.error;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!some) {
      restore.mutate({ backupId: backup.id, clean: clean && !incomplete }, { onSuccess: onDone });
    } else if (picked.length > 0) {
      restoreFiles.mutate({ backupId: backup.id, paths: picked, clean: clean && !incomplete }, { onSuccess: onDone });
    }
  };

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onDone()}
      title={t('backups.restoreTitle', { name: backup.name })}
      size={some ? 'lg' : 'md'}
    >
      <form onSubmit={submit} className="space-y-4" noValidate>
        <fieldset className="space-y-2">
          <legend className="sr-only">{t('backups.restoreWhat')}</legend>
          <Choice name="restore-what" checked={!some} onChange={() => setSome(false)} label={t('backups.restoreAll')} />
          <Choice name="restore-what" checked={some} onChange={() => setSome(true)} label={t('backups.restoreSome')} />
        </fieldset>
        {some ? (
          <BackupFilesPicker serverId={serverId} backupId={backup.id} picked={picked} onChange={setPicked} />
        ) : (
          <p className="text-sm">{t('backups.restoreText', { server: serverName })}</p>
        )}
        <div>
          <Checkbox
            label={t(some ? 'backups.restoreSomeClean' : 'backups.restoreClean')}
            checked={clean && !incomplete}
            disabled={incomplete}
            aria-describedby={hintId}
            onChange={(e) => setClean(e.target.checked)}
          />
          <p id={hintId} className="mt-1 ml-6 text-xs text-muted">
            {incomplete
              ? t('backups.restoreCleanSkipped')
              : t(some ? 'backups.restoreSomeCleanHint' : 'backups.restoreCleanHint')}
          </p>
        </div>
        {!clean && <p className="text-sm text-muted">{t('backups.restoreKeep')}</p>}
        {failed && <Alert>{errorMessage(t, failed)}</Alert>}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onDone}>
            {t('actions.cancel')}
          </Button>
          <Button type="submit" busy={pending} disabled={some && picked.length === 0}>
            {some ? t('backups.restorePicked', { count: picked.length }) : t('backups.restore')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}

function Choice({ label, ...props }: InputHTMLAttributes<HTMLInputElement> & { label: string }) {
  return (
    <label className="flex cursor-pointer items-center gap-2 text-sm">
      <input type="radio" {...props} className="size-4 accent-[var(--pulsar-accent)]" />
      <span>{label}</span>
    </label>
  );
}

/**
 * The backup's folders, one at a time, to pick files and folders from (a folder picked brings all it holds). Picking
 * a folder drops what was picked inside it; at most MAX_PICKED paths.
 */
function BackupFilesPicker({
  serverId,
  backupId,
  picked,
  onChange,
}: {
  serverId: string;
  backupId: string;
  picked: string[];
  onChange: (picked: string[]) => void;
}) {
  const { t } = useTranslation();
  const formatSize = useFormatFileSize();
  const [path, setPath] = useState('');
  const listing = useBackupFiles(serverId, backupId, path);
  const lower = (p: string) => p.toLowerCase();
  const inside = (p: string, folder: string) => lower(p).startsWith(`${lower(folder)}/`);
  const covered = (p: string) => picked.some((q) => lower(q) === lower(p) || inside(p, q));
  const full = picked.length >= MAX_PICKED;

  const toggle = (p: string, on: boolean) =>
    onChange(on ? [...picked.filter((q) => !inside(q, p)), p] : picked.filter((q) => lower(q) !== lower(p)));

  return (
    <div className="space-y-2">
      <nav aria-label={t('files.location')} className="flex min-w-0 flex-wrap items-center gap-1 font-mono text-sm">
        <button type="button" onClick={() => setPath('')} className="rounded px-1 text-accent hover:underline">
          {t('backups.files.top')}
        </button>
        {breadcrumbs(path).map((crumb, i, all) => (
          <span key={crumb.path} className="flex items-center gap-1">
            <span className="text-muted">/</span>
            {i === all.length - 1 ? (
              <span aria-current="location">{visibleName(crumb.name)}</span>
            ) : (
              <button
                type="button"
                onClick={() => setPath(crumb.path)}
                className="rounded px-1 text-accent hover:underline"
              >
                {visibleName(crumb.name)}
              </button>
            )}
          </span>
        ))}
      </nav>
      <div className="max-h-72 overflow-y-auto rounded-md border border-border">
        {listing.isPending ? (
          <div className="p-3">
            <Spinner label={t('common.loading')} />
          </div>
        ) : listing.isError ? (
          <div className="p-3">
            <Alert>{errorMessage(t, listing.error)}</Alert>
          </div>
        ) : listing.data.entries.length === 0 ? (
          <p className="p-3 text-sm text-muted">{t('backups.files.empty')}</p>
        ) : (
          <ul className="divide-y divide-border">
            {listing.data.entries.map((entry) => {
              const entryPath = joinPath(path, entry.name);
              const folder = entry.kind === 'directory';
              const own = picked.some((q) => lower(q) === lower(entryPath));
              const parentPicked = !own && covered(entryPath);
              return (
                <li key={entry.name} className="flex items-center gap-3 px-3 py-2 text-sm">
                  <input
                    type="checkbox"
                    className="size-4 shrink-0 accent-[var(--pulsar-accent)]"
                    aria-label={t('backups.files.pick', { name: visibleName(entry.name) })}
                    checked={own || parentPicked}
                    disabled={parentPicked || (!own && full)}
                    onChange={(e) => toggle(entryPath, e.target.checked)}
                  />
                  {folder ? (
                    <button
                      type="button"
                      className="min-w-0 flex-1 truncate text-left font-medium text-accent hover:underline"
                      onClick={() => setPath(entryPath)}
                    >
                      {visibleName(entry.name)}/
                    </button>
                  ) : (
                    <span className="min-w-0 flex-1 truncate">{visibleName(entry.name)}</span>
                  )}
                  {!folder && (
                    <span className="shrink-0 text-xs text-muted tabular-nums">{formatSize(entry.size)}</span>
                  )}
                </li>
              );
            })}
          </ul>
        )}
      </div>
      {listing.data?.truncated && <p className="text-xs text-muted">{t('backups.files.truncated')}</p>}
      <p className="text-xs text-muted" role="status">
        {full ? t('backups.files.full', { max: MAX_PICKED }) : t('backups.files.picked', { count: picked.length })}
        {picked.length > 0 && (
          <>
            {' · '}
            <button type="button" className="text-accent hover:underline" onClick={() => onChange([])}>
              {t('backups.files.clear')}
            </button>
          </>
        )}
      </p>
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
        {completed && <CopyNote backup={backup} />}
        {backup.status === 'failed' && (backup.errorCode || backup.errorMessage) && (
          <p className="mt-1 text-xs break-words text-danger" title={backup.errorCode ?? undefined}>
            {failureReason(t, backup.errorCode ?? null, backup.errorMessage)}
          </p>
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
        <RowActions name={backup.name}>
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
        </RowActions>
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
function RetentionCard({ limit, save }: { limit: number | null; save: ReturnType<typeof useBackupLimit> }) {
  const { t } = useTranslation();
  const [value, setValue] = useState(limit === null ? '' : String(limit));
  // What the browser cannot read as a number ("1e") it reports as empty, which would keep every backup: refused instead.
  const [unreadable, setUnreadable] = useState(false);
  const parsed = value.trim() === '' ? null : Number(value);
  const invalid = unreadable || (parsed !== null && (!Number.isInteger(parsed) || parsed < 1 || parsed > MAX_LIMIT));
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
            error={
              unreadable
                ? t('backups.retention.notANumber')
                : invalid
                  ? t('backups.retention.invalid', { max: MAX_LIMIT })
                  : undefined
            }
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
                onChange={(e) => {
                  setValue(e.target.value);
                  setUnreadable(e.target.validity.badInput);
                }}
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
  storageId,
  storages,
  save,
}: {
  storageId: string | null;
  storages: { id: string; name: string }[];
  save: ReturnType<typeof useBackupStorageChoice>;
}) {
  const { t } = useTranslation();
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
