import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { api, unwrap, type Schemas } from '../../api/client';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Card } from '../../components/ui/Display';
import { Checkbox, Field, Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { adminKeys, usePermissionCatalog } from '../admin/api';
import { groupPermissions } from '../admin/permissionGroups';
import { useCurrentUser } from './api';

type ApiKey = Schemas['ApiKeyView'];

const apiKeysKey = ['account', 'apiKeys'] as const;
const expiries = ['30', '90', '365', 'never'] as const;

function useApiKeys() {
  return useQuery({ queryKey: apiKeysKey, queryFn: () => unwrap(api.GET('/api/v1/account/api-keys')) });
}

function useKeysChanged() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: apiKeysKey }),
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
}

function useCreateApiKey() {
  const changed = useKeysChanged();
  return useMutation({
    mutationFn: (body: Schemas['CreateApiKeyRequest']) => unwrap(api.POST('/api/v1/account/api-keys', { body })),
    onSuccess: changed,
  });
}

function useRevokeApiKey() {
  const changed = useKeysChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE('/api/v1/account/api-keys/{id}', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

/**
 * The user's API keys (docs/architecture/02, "API keys"): for scripts and tools, with the user's permissions or fewer.
 * A key's value is shown once, when it is created.
 */
export function ApiKeysSection() {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const keys = useApiKeys();
  const revoke = useRevokeApiKey();
  const [creating, setCreating] = useState(false);

  return (
    <Card className="space-y-4 p-6">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <h2 className="text-lg font-semibold">{t('apiKeys.title')}</h2>
          <p className="mt-1 text-sm text-muted">{t('apiKeys.hint')}</p>
        </div>
        <Button variant="secondary" onClick={() => setCreating(true)}>
          {t('apiKeys.create')}
        </Button>
      </div>
      {keys.isPending && <Spinner label={t('common.loading')} />}
      {keys.isError && <Alert>{errorMessage(t, keys.error)}</Alert>}
      {revoke.isError && <Alert>{errorMessage(t, revoke.error)}</Alert>}
      {keys.data?.length === 0 && <p className="text-sm text-muted">{t('apiKeys.empty')}</p>}
      {keys.data && keys.data.length > 0 && (
        <ul aria-label={t('apiKeys.title')} className="divide-y divide-border rounded-md border border-border">
          {keys.data.map((key) => (
            <li key={key.id} className="flex flex-wrap items-center justify-between gap-2 p-3 text-sm">
              <div className="min-w-0">
                <p className="font-medium break-words">{key.name}</p>
                <p className="font-mono text-xs text-muted">{key.prefix}…</p>
                <p className="text-xs text-muted">
                  {key.permissions
                    ? t('apiKeys.limited', { count: key.permissions.length })
                    : t('apiKeys.allPermissions')}
                  {' · '}
                  {key.lastUsedAt
                    ? t('apiKeys.lastUsed', { when: formatDate(key.lastUsedAt) })
                    : t('apiKeys.neverUsed')}
                  {' · '}
                  {key.expiresAt ? <Expiry expiresAt={key.expiresAt} /> : t('apiKeys.noExpiry')}
                </p>
              </div>
              <Button
                variant="ghost"
                size="sm"
                busy={revoke.isPending && revoke.variables === key.id}
                onClick={() => window.confirm(t('apiKeys.revokeConfirm', { name: key.name })) && revoke.mutate(key.id)}
              >
                {t('apiKeys.revoke')}
              </Button>
            </li>
          ))}
        </ul>
      )}
      <CreateApiKeyDialog open={creating} onClose={() => setCreating(false)} />
    </Card>
  );
}

function Expiry({ expiresAt }: { expiresAt: string }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  return new Date(expiresAt) <= new Date() ? (
    <span className="text-danger">{t('apiKeys.expired', { when: formatDate(expiresAt) })}</span>
  ) : (
    <>{t('apiKeys.expires', { when: formatDate(expiresAt) })}</>
  );
}

function CreateApiKeyDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const create = useCreateApiKey();
  const [created, setCreated] = useState<Schemas['CreatedApiKey'] | null>(null);
  const close = () => {
    setCreated(null);
    create.reset();
    onClose();
  };

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => !next && !created && close()}
      title={created ? t('apiKeys.createdTitle') : t('apiKeys.create')}
      size="lg"
    >
      {created ? (
        <CreatedKey value={created.value} onDone={close} />
      ) : (
        <ApiKeyForm create={create} onCreated={setCreated} onCancel={close} />
      )}
    </Dialog>
  );
}

function ApiKeyForm({
  create,
  onCreated,
  onCancel,
}: {
  create: ReturnType<typeof useCreateApiKey>;
  onCreated: (key: Schemas['CreatedApiKey']) => void;
  onCancel: () => void;
}) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const catalog = usePermissionCatalog();
  const [name, setName] = useState('');
  const [expiry, setExpiry] = useState<(typeof expiries)[number]>('90');
  const [limited, setLimited] = useState(false);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  // Only what the user holds somewhere can do anything through a key.
  const held = new Set([
    ...me.permissions,
    ...me.nodePermissions.flatMap((n) => n.permissions),
    ...me.serverPermissions.flatMap((s) => s.permissions),
  ]);
  const groups = groupPermissions(
    (catalog.data ?? []).filter((p) => held.has(p.key)),
    'global',
  );
  const toggle = (key: string, on: boolean) =>
    setSelected((current) => {
      const next = new Set(current);
      if (on) {
        next.add(key);
      } else {
        next.delete(key);
      }
      return next;
    });

  return (
    <form
      className="space-y-4"
      onSubmit={(e) => {
        e.preventDefault();
        create.mutate(
          {
            name: name.trim(),
            permissions: limited ? [...selected] : null,
            expiresInDays: expiry === 'never' ? null : Number(expiry),
          },
          { onSuccess: onCreated },
        );
      }}
    >
      {create.isError && <Alert>{errorMessage(t, create.error)}</Alert>}
      <Field label={t('apiKeys.name')} hint={t('apiKeys.nameHint')}>
        {(ids) => <Input {...ids} required maxLength={64} value={name} onChange={(e) => setName(e.target.value)} />}
      </Field>
      <Field label={t('apiKeys.expiry')}>
        {(ids) => (
          <Select {...ids} value={expiry} onChange={(e) => setExpiry(e.target.value as (typeof expiries)[number])}>
            {expiries.map((value) => (
              <option key={value} value={value}>
                {t(`apiKeys.expiries.${value}`)}
              </option>
            ))}
          </Select>
        )}
      </Field>
      <Checkbox label={t('apiKeys.limit')} checked={limited} onChange={(e) => setLimited(e.target.checked)} />
      {limited && (
        <fieldset className="space-y-3">
          <legend className="sr-only">{t('roles.permissions')}</legend>
          {catalog.isPending && <Spinner label={t('common.loading')} />}
          {groups.map(([group, permissions]) => (
            <div key={group} className="rounded-md border border-border p-3">
              <p className="mb-2 text-xs font-medium tracking-wide text-muted uppercase">
                {t(`permissionGroups.${group}`, { defaultValue: group })}
              </p>
              <div className="grid gap-2 sm:grid-cols-2">
                {permissions.map((p) => (
                  <Checkbox
                    key={p.key}
                    label={t(`permissions.${p.key}`, { defaultValue: p.key })}
                    checked={selected.has(p.key)}
                    onChange={(e) => toggle(p.key, e.target.checked)}
                  />
                ))}
              </div>
            </div>
          ))}
        </fieldset>
      )}
      <div className="flex justify-end gap-2">
        <Button type="button" variant="ghost" onClick={onCancel}>
          {t('actions.cancel')}
        </Button>
        <Button type="submit" busy={create.isPending} disabled={name.trim() === '' || (limited && selected.size === 0)}>
          {t('apiKeys.create')}
        </Button>
      </div>
    </form>
  );
}

/** The new key's value, this once; the user confirms they copied it. */
function CreatedKey({ value, onDone }: { value: string; onDone: () => void }) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);
  return (
    <div className="space-y-4">
      <p className="text-sm">{t('apiKeys.createdIntro')}</p>
      <p aria-label={t('apiKeys.value')} className="rounded-md border border-border p-3 font-mono text-sm break-all">
        {value}
      </p>
      <p className="text-xs text-muted">{t('apiKeys.usage')}</p>
      <div className="flex flex-wrap justify-end gap-2">
        <Button
          variant="secondary"
          onClick={() => void navigator.clipboard?.writeText(value).then(() => setCopied(true))}
        >
          {copied ? t('twoFactor.copied') : t('twoFactor.copy')}
        </Button>
        <Button onClick={onDone}>{t('apiKeys.done')}</Button>
      </div>
    </div>
  );
}

export type { ApiKey };
