import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';
import { ApiError } from '../../api/client';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Badge, Card, Table, Td, Th } from '../../components/ui/Display';
import { Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDate, useFormatFileSize } from '../../lib/format';
import { canOnServer, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import type { ServerDetails } from '../servers/api';
import {
  downloadUrl,
  fileKeys,
  MAX_EDITABLE_BYTES,
  useCreateDirectory,
  useDeleteFile,
  useFileListing,
  useMoveFile,
  writeFile,
  type FileEntry,
} from './api';
import { FileEditor } from './FileEditor';
import { baseName, breadcrumbs, joinPath, nameProblem, parentPath, visibleName } from './paths';

/**
 * The server's files (docs/architecture/07, "Files"): a folder at a time, with the folder and the open file in the
 * address (?path=…, ?file=…) so they survive a reload and can be shared. Links (junctions, symbolic links) are listed
 * but never followed.
 */
export function FilesTab({ details }: { details: ServerDetails }) {
  const me = useCurrentUser();
  const [search, setSearch] = useSearchParams();
  const { server } = details;
  const canEdit = canOnServer(me, server, Permission.ServerFilesEdit);
  const path = search.get('path') ?? '';
  const file = search.get('file');

  const go = (changes: { path?: string; file?: string | null }) => {
    const next = new URLSearchParams(search);
    if (changes.path !== undefined) {
      if (changes.path === '') {
        next.delete('path');
      } else {
        next.set('path', changes.path);
      }
    }
    if (changes.file !== undefined) {
      if (changes.file === null) {
        next.delete('file');
      } else {
        next.set('file', changes.file);
      }
    }
    setSearch(next);
  };

  if (file) {
    return (
      <FileEditor
        key={file}
        serverId={server.id}
        path={file}
        canEdit={canEdit}
        onClose={() => go({ file: null, path: parentPath(file) })}
      />
    );
  }
  return (
    <FileBrowser
      serverId={server.id}
      path={path}
      canEdit={canEdit}
      // Operations that rewrite files (provision, install, update, restore, delete): the node refuses changes meanwhile.
      // A backup only reads them.
      busy={server.activeOperation !== null && server.activeOperation.type !== 'backupCreate'}
      connected={server.nodeConnected}
      onOpenFolder={(folder) => go({ path: folder })}
      onOpenFile={(target) => go({ file: target })}
    />
  );
}

interface FileBrowserProps {
  serverId: string;
  path: string;
  canEdit: boolean;
  busy: boolean;
  /** Changes go through the node: none while it is disconnected. */
  connected: boolean;
  onOpenFolder: (path: string) => void;
  onOpenFile: (path: string) => void;
}

type NameAction = { kind: 'folder' } | { kind: 'file' } | { kind: 'rename'; entry: FileEntry };

function FileBrowser({ serverId, path, canEdit, busy, connected, onOpenFolder, onOpenFile }: FileBrowserProps) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const formatSize = useFormatFileSize();
  const listing = useFileListing(serverId, path);
  const remove = useDeleteFile(serverId);
  const [naming, setNaming] = useState<NameAction | null>(null);
  const uploads = useUploads(serverId, path);
  const input = useRef<HTMLInputElement>(null);
  const blocked = busy || !connected;

  const deleteEntry = (entry: FileEntry) => {
    const message = entry.kind === 'directory' ? 'files.deleteFolderConfirm' : 'files.deleteConfirm';
    if (window.confirm(t(message, { name: visibleName(entry.name) }))) {
      remove.mutate(joinPath(path, entry.name));
    }
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <nav aria-label={t('files.location')} className="flex min-w-0 flex-wrap items-center gap-1 font-mono text-sm">
          <button type="button" onClick={() => onOpenFolder('')} className="rounded px-1 text-accent hover:underline">
            {t('files.root')}
          </button>
          {breadcrumbs(path).map((crumb, i, all) => (
            <span key={crumb.path} className="flex items-center gap-1">
              <span className="text-muted">/</span>
              {i === all.length - 1 ? (
                <span aria-current="location">{visibleName(crumb.name)}</span>
              ) : (
                <button
                  type="button"
                  onClick={() => onOpenFolder(crumb.path)}
                  className="rounded px-1 text-accent hover:underline"
                >
                  {visibleName(crumb.name)}
                </button>
              )}
            </span>
          ))}
        </nav>
        <div className="flex flex-wrap gap-2">
          <Button variant="ghost" busy={listing.isFetching} onClick={() => void listing.refetch()}>
            {t('files.refresh')}
          </Button>
          {canEdit && (
            <>
              <Button variant="secondary" disabled={blocked} onClick={() => setNaming({ kind: 'folder' })}>
                {t('files.newFolder')}
              </Button>
              <Button variant="secondary" disabled={blocked} onClick={() => setNaming({ kind: 'file' })}>
                {t('files.newFile')}
              </Button>
              <Button disabled={blocked} onClick={() => input.current?.click()}>
                {t('files.upload')}
              </Button>
              <input
                ref={input}
                type="file"
                multiple
                hidden
                aria-label={t('files.upload')}
                onChange={(event) => {
                  uploads.start(Array.from(event.target.files ?? []));
                  event.target.value = '';
                }}
              />
            </>
          )}
        </div>
      </div>

      {canEdit && !connected && <p className="text-sm text-muted">{t('files.nodeOffline')}</p>}
      {busy && <p className="text-sm text-muted">{t('files.busy')}</p>}
      {uploads.items.length > 0 && <UploadList uploads={uploads} />}
      {remove.isError && <Alert>{errorMessage(t, remove.error)}</Alert>}

      {listing.isPending && <Spinner label={t('common.loading')} />}
      {listing.isError && <Alert>{errorMessage(t, listing.error)}</Alert>}
      {listing.data && (
        <>
          {listing.data.truncated && (
            <p className="text-sm text-muted">{t('files.truncated', { count: listing.data.entries.length })}</p>
          )}
          <Table label={t('files.title')}>
            <thead>
              <tr>
                <Th>{t('files.name')}</Th>
                <Th className="hidden text-right sm:table-cell">{t('files.size')}</Th>
                <Th className="hidden xl:table-cell">{t('files.modified')}</Th>
                <Th className="text-right">
                  <span className="sr-only">{t('files.actions')}</span>
                </Th>
              </tr>
            </thead>
            <tbody>
              {path !== '' && (
                <tr>
                  <Td>
                    <button
                      type="button"
                      onClick={() => onOpenFolder(parentPath(path))}
                      className="font-mono text-accent hover:underline"
                    >
                      ..
                    </button>
                  </Td>
                  <Td />
                  <Td className="hidden sm:table-cell" />
                  <Td className="hidden xl:table-cell" />
                </tr>
              )}
              {listing.data.entries.length === 0 && (
                <tr>
                  <Td className="text-muted">{t('files.empty')}</Td>
                  <Td />
                  <Td className="hidden sm:table-cell" />
                  <Td className="hidden xl:table-cell" />
                </tr>
              )}
              {listing.data.entries.map((entry) => {
                const target = joinPath(path, entry.name);
                const editable = entry.kind === 'file' && entry.size <= MAX_EDITABLE_BYTES;
                return (
                  <tr key={entry.name}>
                    <Td className="max-w-md">
                      <span className="flex min-w-0 items-center gap-2">
                        <span aria-hidden className="w-4 shrink-0 text-muted">
                          {entry.kind === 'directory' ? '▸' : entry.kind === 'link' ? '↪' : '·'}
                        </span>
                        {entry.kind === 'directory' ? (
                          <button
                            type="button"
                            onClick={() => onOpenFolder(target)}
                            className="truncate font-mono text-accent hover:underline"
                          >
                            {visibleName(entry.name)}/
                          </button>
                        ) : editable ? (
                          <button
                            type="button"
                            onClick={() => onOpenFile(target)}
                            className="truncate font-mono hover:underline"
                          >
                            {visibleName(entry.name)}
                          </button>
                        ) : (
                          <span className="truncate font-mono">{visibleName(entry.name)}</span>
                        )}
                        {entry.kind === 'link' && <Badge>{t('files.link')}</Badge>}
                      </span>
                    </Td>
                    <Td className="hidden text-right sm:table-cell whitespace-nowrap tabular-nums text-muted">
                      {entry.kind === 'file' ? formatSize(entry.size) : ''}
                    </Td>
                    <Td className="hidden whitespace-nowrap text-muted xl:table-cell">
                      {formatDate(entry.modifiedAt)}
                    </Td>
                    <Td className="text-right whitespace-nowrap">
                      <span className="inline-flex gap-1">
                        {entry.kind === 'file' && (
                          <a
                            href={downloadUrl(serverId, target)}
                            className="inline-flex h-8 items-center rounded-md px-3 text-sm font-medium text-accent hover:bg-surface-raised"
                            aria-label={t('files.downloadNamed', { name: visibleName(entry.name) })}
                          >
                            {t('files.download')}
                          </a>
                        )}
                        {canEdit && (
                          <>
                            <Button
                              variant="ghost"
                              size="sm"
                              disabled={blocked}
                              aria-label={t('files.renameNamed', { name: visibleName(entry.name) })}
                              onClick={() => setNaming({ kind: 'rename', entry })}
                            >
                              {t('files.rename')}
                            </Button>
                            <Button
                              variant="ghost"
                              size="sm"
                              disabled={blocked || (remove.isPending && remove.variables === target)}
                              aria-label={t('files.deleteNamed', { name: visibleName(entry.name) })}
                              onClick={() => deleteEntry(entry)}
                            >
                              {t('actions.delete')}
                            </Button>
                          </>
                        )}
                      </span>
                    </Td>
                  </tr>
                );
              })}
            </tbody>
          </Table>
        </>
      )}

      {naming && (
        <NameDialog
          serverId={serverId}
          directory={path}
          action={naming}
          onDone={(created) => {
            setNaming(null);
            if (created) {
              onOpenFile(created);
            }
          }}
        />
      )}
    </div>
  );
}

interface NameDialogProps {
  serverId: string;
  directory: string;
  action: NameAction;
  /** Closes the dialog; with the path of a new file to open it in the editor. */
  onDone: (createdFile?: string) => void;
}

function NameDialog({ serverId, directory, action, onDone }: NameDialogProps) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const createDirectory = useCreateDirectory(serverId);
  const move = useMoveFile(serverId);
  const [name, setName] = useState(action.kind === 'rename' ? action.entry.name : '');
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [touched, setTouched] = useState(false);
  const problem = nameProblem(name);
  const pending = createDirectory.isPending || move.isPending || creating;
  const title =
    action.kind === 'folder'
      ? t('files.newFolder')
      : action.kind === 'file'
        ? t('files.newFile')
        : t('files.renameTitle', { name: visibleName(action.entry.name) });

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setTouched(true);
    if (problem) {
      return;
    }
    setError(null);
    const target = joinPath(directory, name);
    try {
      if (action.kind === 'folder') {
        await createDirectory.mutateAsync(target);
        onDone();
      } else if (action.kind === 'rename') {
        if (name !== action.entry.name) {
          await move.mutateAsync({ path: joinPath(directory, action.entry.name), destination: target });
        }
        onDone();
      } else {
        setCreating(true);
        await writeFile(serverId, target, new Blob([]), { createOnly: true });
        await queryClient.invalidateQueries({ queryKey: fileKeys.all(serverId) });
        onDone(target);
      }
    } catch (caught) {
      setError(caught);
    } finally {
      setCreating(false);
    }
  };

  return (
    <Dialog open onOpenChange={(open) => !open && onDone()} title={title}>
      <form onSubmit={(event) => void submit(event)} className="space-y-4" noValidate>
        {error !== null && <Alert>{errorMessage(t, error)}</Alert>}
        <Field label={t('files.name')} error={touched && problem ? t(`files.nameProblems.${problem}`) : undefined}>
          {(ids) => (
            <Input {...ids} autoFocus value={name} onChange={(e) => setName(e.target.value)} className="font-mono" />
          )}
        </Field>
        <div className="flex justify-end gap-2">
          <Button type="button" variant="secondary" onClick={() => onDone()}>
            {t('actions.cancel')}
          </Button>
          <Button type="submit" busy={pending}>
            {action.kind === 'rename' ? t('files.rename') : t('actions.create')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}

interface Upload {
  id: number;
  name: string;
  /** The folder it goes to: the one open when it was chosen, not the one open when it is retried. */
  directory: string;
  file: File;
  progress: number;
  state: 'uploading' | 'done' | 'failed' | 'exists';
  error?: unknown;
}

/** Files uploaded into the folder one after another, with their progress; an existing name asks before replacing. */
function useUploads(serverId: string, directory: string) {
  const queryClient = useQueryClient();
  const [items, setItems] = useState<Upload[]>([]);
  const counter = useRef(0);
  const queue = useRef(Promise.resolve());

  const update = (id: number, change: Partial<Upload>) =>
    setItems((current) => current.map((item) => (item.id === id ? { ...item, ...change } : item)));

  const run = ({ id, file, directory: target }: Upload, overwrite: boolean) => {
    queue.current = queue.current.then(async () => {
      update(id, { state: 'uploading', progress: 0, error: undefined });
      try {
        await writeFile(serverId, joinPath(target, file.name), file, {
          createOnly: !overwrite,
          onProgress: (progress) => update(id, { progress }),
        });
        update(id, { state: 'done', progress: 1 });
        await queryClient.invalidateQueries({ queryKey: fileKeys.listing(serverId, target) });
      } catch (error) {
        update(
          id,
          error instanceof ApiError && error.code === 'files.exists' ? { state: 'exists' } : { state: 'failed', error },
        );
      }
    });
  };

  return {
    // A question about a name is asked in the folder it is about: elsewhere it would read as about that folder's files.
    items: items.filter((item) => item.state !== 'exists' || item.directory === directory),
    start: (files: File[]) => {
      const added = files.map((file) => ({
        id: ++counter.current,
        name: file.name,
        directory,
        file,
        progress: 0,
        state: 'uploading' as const,
      }));
      setItems((current) => [
        ...current.filter((item) => item.state === 'uploading' || item.state === 'exists'),
        ...added,
      ]);
      for (const item of added) {
        run(item, false);
      }
    },
    replace: (item: Upload) => run(item, true),
    dismiss: (item: Upload) => setItems((current) => current.filter((other) => other.id !== item.id)),
  };
}

function UploadList({ uploads }: { uploads: ReturnType<typeof useUploads> }) {
  const { t } = useTranslation();
  return (
    <Card className="divide-y divide-border">
      {uploads.items.map((item) => {
        const percent = Math.round(item.progress * 100);
        return (
          <div key={item.id} className="space-y-2 px-4 py-3">
            <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
              <span className="truncate font-mono">{visibleName(baseName(item.name))}</span>
              <span className="text-muted">
                {item.state === 'uploading' && t('files.uploads.progress', { percent })}
                {item.state === 'done' && t('files.uploads.done')}
              </span>
            </div>
            {item.state === 'uploading' && (
              <div
                role="progressbar"
                aria-label={t('files.uploads.label', { name: visibleName(item.name) })}
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={percent}
                className="h-1.5 overflow-hidden rounded-full bg-bg"
              >
                <div className="h-full rounded-full bg-accent transition-[width]" style={{ width: `${percent}%` }} />
              </div>
            )}
            {item.state === 'exists' && (
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <span>{t('files.uploads.exists')}</span>
                <Button size="sm" variant="danger" onClick={() => uploads.replace(item)}>
                  {t('files.uploads.replace')}
                </Button>
                <Button size="sm" variant="secondary" onClick={() => uploads.dismiss(item)}>
                  {t('files.uploads.skip')}
                </Button>
              </div>
            )}
            {item.state === 'failed' && <Alert>{errorMessage(t, item.error)}</Alert>}
          </div>
        );
      })}
    </Card>
  );
}
