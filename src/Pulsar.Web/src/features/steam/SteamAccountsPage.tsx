import { useMutation } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { api, unwrap } from '../../api/client';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useSteamAccounts, useSteamAccountsChanged, type SteamAccount, type SteamAccountRequest } from './api';

/** Steam accounts SteamCMD signs in with (settings.manage); servers choose one in their settings. */
export function SteamAccountsPage() {
  const { t } = useTranslation();
  const accounts = useSteamAccounts();
  const [editing, setEditing] = useState<SteamAccount | 'new' | null>(null);

  return (
    <>
      <PageHeader
        title={t('steamAccounts.title')}
        description={t('steamAccounts.description')}
        actions={<Button onClick={() => setEditing('new')}>{t('steamAccounts.add')}</Button>}
      />
      {accounts.isPending && <Spinner label={t('common.loading')} />}
      {accounts.isError && <Alert>{errorMessage(t, accounts.error)}</Alert>}
      {accounts.data && accounts.data.accounts.length === 0 && (
        <p className="text-sm text-muted">{t('steamAccounts.empty')}</p>
      )}
      {accounts.data && accounts.data.accounts.length > 0 && (
        <Table label={t('steamAccounts.title')}>
          <thead>
            <tr>
              <Th>{t('steamAccounts.name')}</Th>
              <Th>{t('steamAccounts.userName')}</Th>
              <Th>{t('steamAccounts.use')}</Th>
              <Th>
                <span className="sr-only">{t('common.actions')}</span>
              </Th>
            </tr>
          </thead>
          <tbody>
            {accounts.data.accounts.map((account) => (
              <tr key={account.id}>
                <Td className="font-medium">{account.name}</Td>
                <Td className="font-mono">{account.userName}</Td>
                <Td>{t('steamAccounts.servers', { count: account.servers })}</Td>
                <Td className="text-right">
                  <Button variant="ghost" size="sm" onClick={() => setEditing(account)}>
                    {t('actions.edit')}
                  </Button>
                </Td>
              </tr>
            ))}
          </tbody>
        </Table>
      )}
      <p className="mt-6 max-w-2xl text-sm text-muted">{t('steamAccounts.hint')}</p>
      {editing && <AccountDialog account={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </>
  );
}

function AccountDialog({ account, onClose }: { account: SteamAccount | null; onClose: () => void }) {
  const { t } = useTranslation();
  const changed = useSteamAccountsChanged();
  const save = useMutation({
    mutationFn: (body: SteamAccountRequest) =>
      account
        ? unwrap(api.PUT('/api/v1/steam-accounts/{accountId}', { params: { path: { accountId: account.id } }, body }))
        : unwrap(api.POST('/api/v1/steam-accounts', { body })),
    onSuccess: async () => {
      await changed();
      onClose();
    },
  });
  const remove = useMutation({
    mutationFn: (accountId: string) =>
      unwrap(api.DELETE('/api/v1/steam-accounts/{accountId}', { params: { path: { accountId } } })),
    onSuccess: async () => {
      await changed();
      onClose();
    },
  });
  const [name, setName] = useState(account?.name ?? '');
  const [userName, setUserName] = useState(account?.userName ?? '');
  const [password, setPassword] = useState('');

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate({ name: name.trim(), userName: userName.trim(), password: password || null });
  };

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={account ? t('steamAccounts.edit') : t('steamAccounts.add')}
    >
      <form className="space-y-4" onSubmit={submit} noValidate>
        <Field label={t('steamAccounts.name')} hint={t('steamAccounts.nameHint')}>
          {(control) => <Input {...control} value={name} maxLength={64} onChange={(e) => setName(e.target.value)} />}
        </Field>
        <Field label={t('steamAccounts.userName')}>
          {(control) => (
            <Input
              {...control}
              autoComplete="off"
              maxLength={64}
              value={userName}
              onChange={(e) => setUserName(e.target.value)}
            />
          )}
        </Field>
        <Field label={t('steamAccounts.password')} hint={account ? t('steamAccounts.passwordKeep') : undefined}>
          {(control) => (
            <Input
              {...control}
              type="password"
              autoComplete="new-password"
              maxLength={64}
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          )}
        </Field>
        <p className="text-xs text-muted">{t('steamAccounts.secondary')}</p>
        {(save.error ?? remove.error) && <Alert>{errorMessage(t, save.error ?? remove.error)}</Alert>}
        <div className="flex items-center justify-between gap-2">
          {account ? (
            <Button
              type="button"
              variant="danger"
              disabled={remove.isPending}
              onClick={() =>
                window.confirm(t('steamAccounts.deleteConfirm', { name: account.name })) && remove.mutate(account.id)
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
