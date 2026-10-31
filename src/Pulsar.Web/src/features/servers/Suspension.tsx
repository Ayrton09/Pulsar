import { Ban } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert } from '../../components/ui/Display';
import { Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { canOnServer, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useSuspendServer, useUnsuspendServer, type ServerDetails } from './api';

/** Longest reason the panel keeps (the API's Server.MaxSuspensionReasonLength). */
const maxReason = 200;

/**
 * A suspended server's notice (Pulsar 1.0.24): since when and why. Whoever may suspend it can lift it from here; its
 * other users see only this, until it is unsuspended.
 */
export function SuspensionNotice({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const formatDate = useFormatDate();
  const unsuspend = useUnsuspendServer();
  const { server } = details;
  if (!server.suspension) {
    return null;
  }

  return (
    <div role="status" className="mb-6 rounded-2xl border border-warning/40 bg-warning/10 p-4 sm:p-5">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="flex min-w-0 gap-3">
          <Ban aria-hidden="true" className="mt-0.5 size-5 shrink-0 text-warning" />
          <div className="min-w-0">
            <p className="font-semibold">{t('servers.suspension.title')}</p>
            <p className="mt-1 text-sm text-muted">
              {t('servers.suspension.since', { date: formatDate(server.suspension.at) })}
            </p>
            {server.suspension.reason && (
              <p className="mt-2 text-sm break-words">
                {t('servers.suspension.reason', { reason: server.suspension.reason })}
              </p>
            )}
            {!canOnServer(me, server, Permission.ServerSuspend) && (
              <p className="mt-2 text-sm text-muted">{t('servers.suspension.contact')}</p>
            )}
          </div>
        </div>
        {canOnServer(me, server, Permission.ServerSuspend) && (
          <Button
            variant="secondary"
            busy={unsuspend.isPending}
            onClick={() => unsuspend.mutate(server.id)}
            className="shrink-0"
          >
            {t('servers.suspension.unsuspend')}
          </Button>
        )}
      </div>
      {unsuspend.isError && (
        <div className="mt-3">
          <Alert>{errorMessage(t, unsuspend.error)}</Alert>
        </div>
      )}
    </div>
  );
}

/** Suspending a server: an optional reason its users will see. */
export function SuspendServerDialog({ details, onClose }: { details: ServerDetails; onClose: () => void }) {
  const { t } = useTranslation();
  const suspend = useSuspendServer();
  const [reason, setReason] = useState('');
  const submit = () => suspend.mutate({ id: details.server.id, reason: reason.trim() || null }, { onSuccess: onClose });

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()} title={t('servers.suspension.suspend')}>
      <div className="space-y-4">
        <p className="text-sm text-muted">{t('servers.suspension.hint', { name: details.server.name })}</p>
        {suspend.isError && <Alert>{errorMessage(t, suspend.error)}</Alert>}
        <Field label={t('servers.suspension.reasonLabel')} hint={t('servers.suspension.reasonHint')}>
          {(ids) => <Input {...ids} value={reason} maxLength={maxReason} onChange={(e) => setReason(e.target.value)} />}
        </Field>
        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button variant="danger" busy={suspend.isPending} onClick={submit}>
            {t('servers.suspension.suspend')}
          </Button>
        </div>
      </div>
    </Dialog>
  );
}
