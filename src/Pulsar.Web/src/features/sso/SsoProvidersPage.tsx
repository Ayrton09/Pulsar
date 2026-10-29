import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Card } from '../../components/ui/Display';
import { Checkbox, Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { CopyButton } from '../sftp/CopyButton';
import { ssoName, useSsoProviders, useUpdateSsoProvider, type SsoProvider } from './api';

/**
 * The sign-in providers (Pulsar 1.0.18; settings.manage): Discord, Google and one OpenID Connect provider, each the
 * administrator's application registered with it. The address to give it as its redirect is shown to copy. Users then
 * link their accounts from their own page; nobody new gets in through a provider.
 */
export function SsoProvidersPage() {
  const { t } = useTranslation();
  const providers = useSsoProviders();
  // Here, not in the cards: a save remounts its card with what the panel stored, and the notice must survive that.
  const [saved, setSaved] = useState<SsoProvider['kind'] | null>(null);
  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold">{t('sso.admin.title')}</h1>
        <p className="mt-1 max-w-2xl text-sm text-muted">{t('sso.admin.description')}</p>
      </div>
      {providers.isPending && <Spinner label={t('common.loading')} />}
      {providers.isError && <Alert>{errorMessage(t, providers.error)}</Alert>}
      {providers.data?.map((provider) => (
        // Keyed by what is stored: saved (or changed by another admin), the form starts again from it.
        <ProviderCard
          key={`${provider.kind}-${provider.updatedAt ?? 'new'}`}
          provider={provider}
          saved={saved === provider.kind}
          onSaved={() => setSaved(provider.kind)}
        />
      ))}
    </div>
  );
}

function ProviderCard({ provider, saved, onSaved }: { provider: SsoProvider; saved: boolean; onSaved: () => void }) {
  const { t } = useTranslation();
  const save = useUpdateSsoProvider();
  const [enabled, setEnabled] = useState(provider.enabled);
  const [clientId, setClientId] = useState(provider.clientId);
  const [secret, setSecret] = useState('');
  const [authority, setAuthority] = useState(provider.authority ?? '');
  const [name, setName] = useState(provider.displayName ?? '');
  const oidc = provider.kind === 'oidc';
  const callback = `${window.location.origin}${provider.callbackPath}`;
  const title = oidc ? t('sso.admin.oidc') : ssoName(provider.kind);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate(
      {
        kind: provider.kind,
        enabled,
        clientId: clientId.trim() || null,
        clientSecret: secret || null,
        authority: oidc ? authority.trim() || null : null,
        displayName: oidc ? name.trim() || null : null,
      },
      {
        onSuccess: () => {
          setSecret('');
          onSaved();
        },
      },
    );
  };

  return (
    <Card className="p-6">
      <form onSubmit={submit} className="space-y-4" noValidate aria-label={title}>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="text-lg font-semibold">{title}</h2>
          <Checkbox label={t('sso.admin.enabled')} checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        </div>
        <p className="text-sm text-muted">{t(`sso.admin.hints.${provider.kind}`)}</p>
        <div className="space-y-1">
          <p className="text-sm font-medium">{t('sso.admin.redirect')}</p>
          <div className="flex min-w-0 items-center gap-2">
            <code className="min-w-0 rounded bg-surface-raised px-2 py-1 font-mono text-xs break-all">{callback}</code>
            <CopyButton text={callback} label={t('sso.admin.copy')} />
          </div>
        </div>
        {oidc && (
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t('sso.admin.authority')} hint={t('sso.admin.authorityHint')}>
              {(ids) => (
                <Input
                  {...ids}
                  type="url"
                  maxLength={255}
                  placeholder="https://auth.example.com/realms/games"
                  value={authority}
                  onChange={(e) => setAuthority(e.target.value)}
                />
              )}
            </Field>
            <Field label={t('sso.admin.buttonName')} hint={t('sso.admin.buttonNameHint')}>
              {(ids) => <Input {...ids} maxLength={32} value={name} onChange={(e) => setName(e.target.value)} />}
            </Field>
          </div>
        )}
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t('sso.admin.clientId')}>
            {(ids) => (
              <Input
                {...ids}
                autoComplete="off"
                maxLength={256}
                value={clientId}
                onChange={(e) => setClientId(e.target.value)}
              />
            )}
          </Field>
          <Field label={t('sso.admin.clientSecret')} hint={provider.hasSecret ? t('sso.admin.secretKeep') : undefined}>
            {(ids) => (
              <Input
                {...ids}
                type="password"
                autoComplete="new-password"
                maxLength={512}
                value={secret}
                onChange={(e) => setSecret(e.target.value)}
              />
            )}
          </Field>
        </div>
        {save.isError && <Alert>{errorMessage(t, save.error)}</Alert>}
        {saved && !save.isError && (
          <p role="status" className="text-sm text-success">
            {t('sso.admin.saved')}
          </p>
        )}
        <Button type="submit" variant="secondary" busy={save.isPending}>
          {t('actions.save')}
        </Button>
      </form>
    </Card>
  );
}
