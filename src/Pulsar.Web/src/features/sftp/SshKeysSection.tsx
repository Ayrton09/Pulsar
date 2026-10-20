import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Card } from '../../components/ui/Display';
import { Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { useCurrentUser } from '../auth/api';
import { sshKeyComment, sshKeyType, useAddSshKey, useDeleteSshKey, useSshKeys } from './api';

/**
 * The user's SSH keys for SFTP (docs/guides/sftp.md, "SSH keys"). A key signs in on its own, a two-factor user too, so
 * adding one takes the password and, with two-factor authentication on, the code from the app. Each is listed with its
 * fingerprint as OpenSSH prints it; deleting one ends the SFTP sessions it signed in.
 */
export function SshKeysSection() {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const keys = useSshKeys();
  const remove = useDeleteSshKey();
  const [adding, setAdding] = useState(false);

  return (
    <Card className="space-y-4 p-6">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <h2 className="text-lg font-semibold">{t('sshKeys.title')}</h2>
          <p className="mt-1 text-sm text-muted">{t('sshKeys.hint')}</p>
        </div>
        <Button variant="secondary" onClick={() => setAdding(true)}>
          {t('sshKeys.add')}
        </Button>
      </div>
      {keys.isPending && <Spinner label={t('common.loading')} />}
      {keys.isError && <Alert>{errorMessage(t, keys.error)}</Alert>}
      {remove.isError && <Alert>{errorMessage(t, remove.error)}</Alert>}
      {keys.data?.length === 0 && (
        <div className="space-y-2 text-sm text-muted">
          <p>{t('sshKeys.empty')}</p>
          <p>{t('sshKeys.howTo')}</p>
          <ul className="list-disc space-y-1 pl-5 text-xs">
            <li>{t('sshKeys.puttygen')}</li>
            <li>
              {t('sshKeys.sshKeygen')}
              <code className="mt-1 block font-mono">ssh-keygen -t ed25519</code>
            </li>
          </ul>
        </div>
      )}
      {keys.data && keys.data.length > 0 && (
        <ul aria-label={t('sshKeys.title')} className="divide-y divide-border rounded-md border border-border">
          {keys.data.map((key) => (
            <li key={key.id} className="flex flex-wrap items-center justify-between gap-2 p-3 text-sm">
              <div className="min-w-0">
                <p className="font-medium break-words">{key.name}</p>
                <p className="text-xs text-muted">{sshKeyType(key.type, key.bits)}</p>
                <p className="font-mono text-xs break-all text-muted" title={t('sshKeys.fingerprint')}>
                  {key.fingerprint}
                </p>
                <p className="text-xs text-muted">
                  {t('sshKeys.added', { when: formatDate(key.createdAt) })}
                  {' · '}
                  {key.lastUsedAt
                    ? t('sshKeys.lastUsed', { when: formatDate(key.lastUsedAt) })
                    : t('sshKeys.neverUsed')}
                </p>
              </div>
              <Button
                variant="ghost"
                size="sm"
                busy={remove.isPending && remove.variables === key.id}
                onClick={() => window.confirm(t('sshKeys.deleteConfirm', { name: key.name })) && remove.mutate(key.id)}
              >
                {t('sshKeys.delete')}
              </Button>
            </li>
          ))}
        </ul>
      )}
      {adding && <AddSshKeyDialog onClose={() => setAdding(false)} />}
    </Card>
  );
}

function AddSshKeyDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const add = useAddSshKey();
  const [name, setName] = useState('');
  const [publicKey, setPublicKey] = useState('');
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const comment = sshKeyComment(publicKey);
  const close = () => {
    add.reset(); // the password and the code were its variables
    onClose();
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    add.mutate(
      {
        name: name.trim() || null,
        publicKey: publicKey.trim(),
        password,
        code: me.twoFactorEnabled ? code.replace(/[\s-]/g, '') : null,
      },
      { onSuccess: close },
    );
  };

  return (
    <Dialog
      open
      onOpenChange={(next) => !next && !add.isPending && close()}
      title={t('sshKeys.add')}
      description={t('sshKeys.confirmHint')}
      size="lg"
    >
      <form onSubmit={submit} className="space-y-4" noValidate>
        {add.isError && <Alert>{errorMessage(t, add.error)}</Alert>}
        <Field label={t('sshKeys.publicKey')} hint={t('sshKeys.publicKeyHint')}>
          {(ids) => (
            <textarea
              {...ids}
              rows={4}
              value={publicKey}
              spellCheck={false}
              autoComplete="off"
              placeholder="ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAA… you@laptop"
              onChange={(e) => setPublicKey(e.target.value)}
              className="w-full rounded-md border border-border bg-bg px-3 py-2 font-mono text-xs break-all"
            />
          )}
        </Field>
        <Field label={t('sshKeys.name')} hint={t('sshKeys.nameHint')}>
          {(ids) => (
            <Input
              {...ids}
              maxLength={64}
              value={name}
              placeholder={comment.length <= 64 ? comment : ''}
              onChange={(e) => setName(e.target.value)}
            />
          )}
        </Field>
        <Field label={t('account.currentPassword')}>
          {(ids) => (
            <Input
              {...ids}
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          )}
        </Field>
        {me.twoFactorEnabled && (
          <Field label={t('twoFactor.appCode')} hint={t('sshKeys.codeHint')}>
            {(ids) => (
              <Input
                {...ids}
                value={code}
                onChange={(e) => setCode(e.target.value)}
                inputMode="numeric"
                autoComplete="one-time-code"
                maxLength={9}
              />
            )}
          </Field>
        )}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" disabled={add.isPending} onClick={close}>
            {t('actions.cancel')}
          </Button>
          <Button
            type="submit"
            busy={add.isPending}
            disabled={
              publicKey.trim() === '' ||
              password.length === 0 ||
              (me.twoFactorEnabled && code.replace(/[\s-]/g, '').length !== 6)
            }
          >
            {t('sshKeys.add')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
