import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { api, unwrap, type Schemas } from '../../api/client';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { Checkbox, Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { adminKeys } from '../admin/api';

type Storage = Schemas['BackupStorageView'];
type StorageRequest = Schemas['BackupStorageRequest'];

const storagesKey = ['backup-storages'] as const;

function useStorages() {
  return useQuery({ queryKey: storagesKey, queryFn: () => unwrap(api.GET('/api/v1/backup-storages')) });
}

function useStorageChanged() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: storagesKey }),
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
}

/** S3-compatible storages backups are copied to (settings.manage); servers choose one in their Backups tab. */
export function BackupStoragesPage() {
  const { t } = useTranslation();
  const storages = useStorages();
  const [editing, setEditing] = useState<Storage | 'new' | null>(null);

  return (
    <>
      <PageHeader
        title={t('storage.title')}
        description={t('storage.description')}
        actions={<Button onClick={() => setEditing('new')}>{t('storage.add')}</Button>}
      />
      {storages.isPending && <Spinner label={t('common.loading')} />}
      {storages.isError && <Alert>{errorMessage(t, storages.error)}</Alert>}
      {storages.data && storages.data.storages.length === 0 && (
        <p className="text-sm text-muted">{t('storage.empty')}</p>
      )}
      {storages.data && storages.data.storages.length > 0 && (
        <Table label={t('storage.title')}>
          <thead>
            <tr>
              <Th>{t('storage.name')}</Th>
              <Th>{t('storage.where')}</Th>
              <Th>{t('storage.use')}</Th>
              <Th>
                <span className="sr-only">{t('common.actions')}</span>
              </Th>
            </tr>
          </thead>
          <tbody>
            {storages.data.storages.map((storage) => (
              <tr key={storage.id}>
                <Td className="font-medium">{storage.name}</Td>
                <Td>
                  <p>{storage.endpoint ?? t('storage.aws', { region: storage.region })}</p>
                  <p className="text-xs text-muted">
                    {storage.bucket}/{storage.prefix}
                  </p>
                </Td>
                <Td>
                  {t('storage.servers', { count: storage.servers })} · {t('storage.copies', { count: storage.copies })}
                </Td>
                <Td className="text-right">
                  <Button variant="ghost" size="sm" onClick={() => setEditing(storage)}>
                    {t('actions.edit')}
                  </Button>
                </Td>
              </tr>
            ))}
          </tbody>
        </Table>
      )}
      {editing && <StorageDialog storage={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </>
  );
}

function StorageDialog({ storage, onClose }: { storage: Storage | null; onClose: () => void }) {
  const { t } = useTranslation();
  const changed = useStorageChanged();
  const save = useMutation({
    mutationFn: (body: StorageRequest) =>
      storage
        ? unwrap(api.PUT('/api/v1/backup-storages/{storageId}', { params: { path: { storageId: storage.id } }, body }))
        : unwrap(api.POST('/api/v1/backup-storages', { body })),
    onSuccess: async () => {
      await changed();
      onClose();
    },
  });
  const remove = useMutation({
    mutationFn: (id: string) =>
      unwrap(api.DELETE('/api/v1/backup-storages/{storageId}', { params: { path: { storageId: id } } })),
    onSuccess: async () => {
      await changed();
      onClose();
    },
  });
  const [name, setName] = useState(storage?.name ?? '');
  const [endpoint, setEndpoint] = useState(storage?.endpoint ?? '');
  const [region, setRegion] = useState(storage?.region ?? 'auto');
  const [bucket, setBucket] = useState(storage?.bucket ?? '');
  const [prefix, setPrefix] = useState(storage?.prefix ?? 'pulsar/');
  const [accessKeyId, setAccessKeyId] = useState(storage?.accessKeyId ?? '');
  const [secret, setSecret] = useState('');
  const [pathStyle, setPathStyle] = useState(storage?.pathStyle ?? true);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate({
      name: name.trim(),
      endpoint: endpoint.trim() || null,
      region: region.trim(),
      bucket: bucket.trim(),
      prefix: prefix.trim() || null,
      accessKeyId: accessKeyId.trim(),
      secretAccessKey: secret || null,
      pathStyle,
    });
  };

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={storage ? t('storage.edit') : t('storage.add')}
      size="lg"
    >
      <form className="space-y-4" onSubmit={submit} noValidate>
        <Field label={t('storage.name')}>
          {(control) => <Input {...control} value={name} maxLength={64} onChange={(e) => setName(e.target.value)} />}
        </Field>
        <Field label={t('storage.endpoint')} hint={t('storage.endpointHint')}>
          {(control) => (
            <Input
              {...control}
              type="url"
              placeholder="https://<account>.r2.cloudflarestorage.com"
              value={endpoint}
              onChange={(e) => setEndpoint(e.target.value)}
            />
          )}
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t('storage.region')} hint={t('storage.regionHint')}>
            {(control) => <Input {...control} value={region} onChange={(e) => setRegion(e.target.value)} />}
          </Field>
          <Field label={t('storage.bucket')}>
            {(control) => <Input {...control} value={bucket} onChange={(e) => setBucket(e.target.value)} />}
          </Field>
        </div>
        <Field label={t('storage.prefix')} hint={t('storage.prefixHint')}>
          {(control) => <Input {...control} value={prefix} onChange={(e) => setPrefix(e.target.value)} />}
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t('storage.accessKeyId')}>
            {(control) => (
              <Input
                {...control}
                autoComplete="off"
                value={accessKeyId}
                onChange={(e) => setAccessKeyId(e.target.value)}
              />
            )}
          </Field>
          <Field label={t('storage.secret')} hint={storage ? t('storage.secretKeep') : undefined}>
            {(control) => (
              <Input
                {...control}
                type="password"
                autoComplete="new-password"
                value={secret}
                onChange={(e) => setSecret(e.target.value)}
              />
            )}
          </Field>
        </div>
        <Checkbox label={t('storage.pathStyle')} checked={pathStyle} onChange={(e) => setPathStyle(e.target.checked)} />
        <p className="text-xs text-muted">{t('storage.keysHint')}</p>
        {(save.error ?? remove.error) && <Alert>{errorMessage(t, save.error ?? remove.error)}</Alert>}
        <div className="flex items-center justify-between gap-2">
          {storage ? (
            <Button
              type="button"
              variant="danger"
              disabled={remove.isPending}
              onClick={() =>
                window.confirm(t('storage.deleteConfirm', { name: storage.name })) && remove.mutate(storage.id)
              }
            >
              {t('actions.delete')}
            </Button>
          ) : (
            <span />
          )}
          <div className="flex gap-2">
            <Button type="button" variant="ghost" onClick={onClose}>
              {t('actions.cancel')}
            </Button>
            <Button type="submit" busy={save.isPending}>
              {t('actions.save')}
            </Button>
          </div>
        </div>
      </form>
    </Dialog>
  );
}
