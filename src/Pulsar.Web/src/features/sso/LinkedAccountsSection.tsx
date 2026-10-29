import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Card } from '../../components/ui/Display';
import { Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useCurrentUser } from '../auth/api';
import {
  ssoName,
  useSsoLinks,
  useSsoOptions,
  useStartSsoLink,
  useUnlinkSso,
  type SsoKind,
  type SsoOption,
} from './api';

/**
 * The user's accounts of the sign-in providers (Pulsar 1.0.18): linked here with the password (and the code), then
 * a way in on the sign-in page. Only the providers the administrator turned on are offered; a link to one turned off
 * since is still shown, to remove it.
 */
export function LinkedAccountsSection() {
  const { t } = useTranslation();
  const options = useSsoOptions();
  const links = useSsoLinks();
  const unlink = useUnlinkSso();
  const [linking, setLinking] = useState<SsoOption | null>(null);
  const [search, setSearch] = useSearchParams();
  const linked = search.get('sso') === 'linked';
  const failed = search.get('sso_error');
  const kinds = new Set<SsoKind>([
    ...(options.data ?? []).map((o) => o.kind),
    ...(links.data ?? []).map((l) => l.kind),
  ]);
  if (kinds.size === 0 && !failed) {
    return null;
  }

  const dismiss = () => {
    const next = new URLSearchParams(search);
    next.delete('sso');
    next.delete('sso_error');
    setSearch(next, { replace: true });
  };

  return (
    <Card className="space-y-4 p-6">
      <div>
        <h2 className="text-lg font-semibold">{t('sso.linked.title')}</h2>
        <p className="mt-1 text-sm text-muted">{t('sso.linked.hint')}</p>
      </div>
      {linked && (
        <p role="status" className="text-sm text-success">
          {t('sso.linked.done')}{' '}
          <button type="button" className="text-accent hover:underline" onClick={dismiss}>
            {t('actions.close')}
          </button>
        </p>
      )}
      {failed && <Alert>{t(`sso.errors.${failed}`, { defaultValue: t('sso.errors.unknown') })}</Alert>}
      {(options.isPending || links.isPending) && <Spinner label={t('common.loading')} />}
      {links.isError && <Alert>{errorMessage(t, links.error)}</Alert>}
      {unlink.isError && <Alert>{errorMessage(t, unlink.error)}</Alert>}
      {kinds.size > 0 && links.data && (
        <ul aria-label={t('sso.linked.title')} className="divide-y divide-border rounded-md border border-border">
          {[...kinds].sort().map((kind) => {
            const option = options.data?.find((o) => o.kind === kind);
            const link = links.data.find((l) => l.kind === kind);
            const name = ssoName(kind, option?.name);
            return (
              <li key={kind} className="flex flex-wrap items-center justify-between gap-2 p-3 text-sm">
                <div className="min-w-0">
                  <p className="font-medium">{name}</p>
                  <p className="text-xs break-words text-muted">
                    {link ? t('sso.linked.as', { name: link.name ?? '—' }) : t('sso.linked.none')}
                  </p>
                </div>
                {link ? (
                  <Button
                    variant="ghost"
                    size="sm"
                    busy={unlink.isPending && unlink.variables === kind}
                    onClick={() => window.confirm(t('sso.linked.unlinkConfirm', { name })) && unlink.mutate(kind)}
                  >
                    {t('sso.linked.unlink')}
                  </Button>
                ) : (
                  option && (
                    <Button variant="secondary" size="sm" onClick={() => setLinking(option)}>
                      {t('sso.linked.link')}
                    </Button>
                  )
                )}
              </li>
            );
          })}
        </ul>
      )}
      {linking && <LinkDialog option={linking} onClose={() => setLinking(null)} />}
    </Card>
  );
}

function LinkDialog({ option, onClose }: { option: SsoOption; onClose: () => void }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const link = useStartSsoLink();
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const name = ssoName(option.kind, option.name);
  const close = () => {
    link.reset(); // the password and the code were its variables
    onClose();
  };

  // On success the browser goes to the provider, and comes back to this page.
  const submit = (event: FormEvent) => {
    event.preventDefault();
    link.mutate({ kind: option.kind, password, code: me.twoFactorEnabled ? code.replace(/[\s-]/g, '') : null });
  };

  return (
    <Dialog
      open
      onOpenChange={(next) => !next && !link.isPending && close()}
      title={t('sso.linked.linkTitle', { name })}
      description={t('sso.linked.confirmHint', { name })}
    >
      <form onSubmit={submit} className="space-y-4" noValidate>
        {link.isError && <Alert>{errorMessage(t, link.error)}</Alert>}
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
          <Field label={t('twoFactor.appCode')}>
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
          <Button type="button" variant="ghost" disabled={link.isPending} onClick={close}>
            {t('actions.cancel')}
          </Button>
          <Button
            type="submit"
            busy={link.isPending || link.isSuccess}
            disabled={password.length === 0 || (me.twoFactorEnabled && code.replace(/[\s-]/g, '').length !== 6)}
          >
            {t('sso.linked.continue', { name })}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
