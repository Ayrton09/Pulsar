import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { Alert, Badge, Card } from '../../components/ui/Display';
import { Field, Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { canOnServer, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import type { ServerDetails } from '../servers/api';
import { useRetryWithSteamGuard, useSetServerSteamAccount, useSteamAccounts } from './api';

/**
 * The Steam account the server's SteamCMD signs in with (docs/architecture/06, "Steam accounts"). Everyone who sees the
 * server sees which one; choosing takes server.limits.edit, given on a node or globally only (whoever hosts the server
 * decides whose account downloads its game).
 */
export function SteamAccountCard({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const { server } = details;
  const steam = details.steam ?? { required: false, accountId: null, accountName: null };
  const canEdit = canOnServer(me, server, Permission.ServerLimitsEdit);
  const accounts = useSteamAccounts(canEdit);
  const save = useSetServerSteamAccount();
  const [chosen, setChosen] = useState(steam.accountId ?? '');

  return (
    <Card className="p-6">
      <div role="region" aria-labelledby="server-steam" className="space-y-4">
        <div>
          <h2 id="server-steam" className="flex items-center gap-2 text-lg font-semibold">
            {t('servers.steam.title')}
            {steam.required && <Badge tone="accent">{t('servers.steam.required')}</Badge>}
          </h2>
          <p className="mt-1 text-sm text-muted">
            {steam.required ? t('servers.steam.requiredHint') : t('servers.steam.hint')}
          </p>
        </div>
        {save.isError && <Alert>{errorMessage(t, save.error)}</Alert>}
        {save.isSuccess && (
          <p role="status" className="text-sm text-success">
            {t('servers.steam.saved')}
          </p>
        )}
        {canEdit ? (
          <form
            className="flex flex-wrap items-end gap-3"
            onSubmit={(e) => {
              e.preventDefault();
              save.mutate({ id: server.id, steamAccountId: chosen === '' ? null : chosen });
            }}
          >
            <div className="min-w-64 flex-1">
              <Field label={t('servers.steam.account')}>
                {(ids) => (
                  <Select {...ids} value={chosen} onChange={(e) => setChosen(e.target.value)}>
                    <option value="">{t('servers.steam.anonymous')}</option>
                    {accounts.data?.accounts.map((account) => (
                      <option key={account.id} value={account.id}>
                        {account.name} ({account.userName})
                      </option>
                    ))}
                  </Select>
                )}
              </Field>
            </div>
            <Button type="submit" busy={save.isPending}>
              {t('actions.save')}
            </Button>
          </form>
        ) : (
          <p className="text-sm">{steam.accountName ?? t('servers.steam.anonymous')}</p>
        )}
      </div>
    </Card>
  );
}

/**
 * When Steam stopped the server's last install or update to ask for a Steam Guard code (sent by mail, or shown by the
 * Steam mobile app): the code, and the same operation again.
 */
export function SteamGuardPrompt({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const retry = useRetryWithSteamGuard();
  const [code, setCode] = useState('');
  const { server } = details;
  const last = details.recentOperations.find((o) => o.type === 'serverInstall' || o.type === 'serverUpdate');
  if (
    !last ||
    last.status !== 'failed' ||
    last.errorCode !== 'steamcmd.guard_required' ||
    server.activeOperation !== null ||
    !canOnServer(me, server, Permission.ServerInstall)
  ) {
    return null;
  }

  const kind = last.type === 'serverInstall' ? 'install' : 'update';
  const valid = /^[A-Za-z0-9]{5}$/.test(code.trim());
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (valid) {
      retry.mutate({ id: server.id, kind, code: code.trim() }, { onSuccess: () => setCode('') });
    }
  };

  return (
    <section
      aria-label={t('servers.steam.guardTitle')}
      className="mt-4 space-y-3 rounded-xl border border-accent/40 bg-surface p-4"
    >
      <p className="text-sm">{t('servers.steam.guardHint')}</p>
      <form className="flex flex-wrap items-end gap-3" onSubmit={submit}>
        <Field label={t('servers.steam.guardCode')}>
          {(ids) => (
            <Input
              {...ids}
              autoComplete="one-time-code"
              maxLength={5}
              className="w-32 font-mono uppercase"
              value={code}
              onChange={(e) => setCode(e.target.value)}
            />
          )}
        </Field>
        <Button type="submit" disabled={!valid || !server.nodeConnected} busy={retry.isPending}>
          {kind === 'install' ? t('servers.steam.retryInstall') : t('servers.steam.retryUpdate')}
        </Button>
      </form>
      {retry.isError && <Alert>{errorMessage(t, retry.error)}</Alert>}
    </section>
  );
}
