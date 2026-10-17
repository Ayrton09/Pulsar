import { RefreshCw } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Badge, Card } from '../../components/ui/Display';
import { Checkbox, Field, Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import {
  checkPublicIp,
  usePublicIpCertificate,
  useRenewPublicIpCertificate,
  useUpdatePublicIpCertificate,
  type AcmeDirectoryKind,
  type PublicIpCertificate,
} from './api';

/**
 * The public IP certificate (settings.manage): a free Let's Encrypt certificate for the panel's static public IP, so
 * that anyone who opens https://<IP>:<port> from the internet gets the panel with no warning. Turning it on agrees to
 * Let's Encrypt's subscriber agreement; the router must forward port 80 (the check) and the panel's port.
 */
export function PublicIpCertificateSection() {
  const { t } = useTranslation();
  const status = usePublicIpCertificate();
  const data = status.data;

  return (
    <section className="mt-10">
      <h2 className="mb-1 text-lg font-semibold">{t('publicIpCertificate.title')}</h2>
      <p className="mb-4 text-sm text-muted">{t('publicIpCertificate.description')}</p>
      {status.isPending && <Spinner label={t('common.loading')} />}
      {status.isError && <Alert>{errorMessage(t, status.error)}</Alert>}
      {data && (
        <Card className="space-y-6 p-6">
          <PublicIpCertificateForm saved={data} />
          {data.enabled && <PublicIpCertificateStatus status={data} />}
        </Card>
      )}
    </section>
  );
}

const ipErrors = {
  required: 'errors.public_ip_certificate.ip_required',
  invalid: 'errors.public_ip_certificate.ip_invalid',
  notPublic: 'errors.public_ip_certificate.ip_not_public',
} as const;

function PublicIpCertificateForm({ saved }: { saved: PublicIpCertificate }) {
  const { t } = useTranslation();
  const update = useUpdatePublicIpCertificate();
  const [enabled, setEnabled] = useState(saved.enabled);
  const [publicIp, setPublicIp] = useState(saved.publicIp ?? '');
  const [email, setEmail] = useState(saved.email ?? '');
  const [directory, setDirectory] = useState<AcmeDirectoryKind>(saved.directory);
  const [ipProblem, setIpProblem] = useState<keyof typeof ipErrors | null>(null);
  const [done, setDone] = useState(false);
  // The panel's port as this browser reaches it: the one to forward with port 80.
  const port = window.location.port || '443';

  const submit = (event: FormEvent) => {
    event.preventDefault();
    setDone(false);
    const ip = publicIp.trim();
    const check = ip === '' ? (enabled ? 'required' : 'ok') : checkPublicIp(ip);
    if (check !== 'ok') {
      setIpProblem(check);
      return;
    }
    setIpProblem(null);
    // Turning it on is agreeing to Let's Encrypt's terms: said once more before it happens.
    if (enabled && !saved.enabled && !window.confirm(t('publicIpCertificate.enableConfirm', { ip }))) {
      return;
    }
    update.mutate(
      { enabled, publicIp: ip || null, email: email.trim() || null, directory },
      { onSuccess: () => setDone(true) },
    );
  };

  return (
    <form onSubmit={submit} className="space-y-4" noValidate>
      {update.isError && <Alert>{errorMessage(t, update.error)}</Alert>}
      {done && (
        <p role="status" className="text-sm text-success">
          {t('users.saved')}
        </p>
      )}
      <Checkbox
        label={t('publicIpCertificate.enabled')}
        checked={enabled}
        onChange={(e) => setEnabled(e.target.checked)}
      />
      <Field
        label={t('publicIpCertificate.publicIp')}
        hint={t('publicIpCertificate.publicIpHint')}
        error={ipProblem ? t(ipErrors[ipProblem]) : undefined}
      >
        {(ids) => (
          <Input
            {...ids}
            value={publicIp}
            onChange={(e) => setPublicIp(e.target.value)}
            maxLength={45}
            autoComplete="off"
            spellCheck={false}
            placeholder="203.0.113.10"
          />
        )}
      </Field>
      <Field label={t('publicIpCertificate.email')} hint={t('publicIpCertificate.emailHint')}>
        {(ids) => (
          <Input
            {...ids}
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            maxLength={254}
            autoComplete="email"
          />
        )}
      </Field>
      <Field label={t('publicIpCertificate.directory')} hint={t('publicIpCertificate.directoryHint')}>
        {(ids) => (
          <Select {...ids} value={directory} onChange={(e) => setDirectory(e.target.value as AcmeDirectoryKind)}>
            <option value="production">{t('publicIpCertificate.directories.production')}</option>
            <option value="staging">{t('publicIpCertificate.directories.staging')}</option>
          </Select>
        )}
      </Field>
      <p className="text-sm">
        {t('publicIpCertificate.terms')}{' '}
        <a
          href={saved.termsOfServiceUrl}
          target="_blank"
          rel="noreferrer noopener"
          className="text-accent hover:underline"
        >
          {t('publicIpCertificate.termsLink')}
        </a>
      </p>
      <p className="rounded-md border border-border px-3 py-2 text-sm">{t('publicIpCertificate.ports', { port })}</p>
      <div className="flex justify-end">
        <Button type="submit" variant="secondary" busy={update.isPending}>
          {t('publicIpCertificate.save')}
        </Button>
      </div>
    </form>
  );
}

function PublicIpCertificateStatus({ status }: { status: PublicIpCertificate }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const renew = useRenewPublicIpCertificate();
  const certificate = status.certificate;

  return (
    <div className="space-y-3 border-t border-border pt-6">
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone={status.serving ? 'success' : 'danger'}>
          {t(status.serving ? 'publicIpCertificate.serving' : 'publicIpCertificate.notServing')}
        </Badge>
        {certificate?.staging && <Badge>{t('publicIpCertificate.staging')}</Badge>}
        {status.renewing && <Badge tone="accent">{t('publicIpCertificate.renewing')}</Badge>}
        <Badge tone={status.listening ? 'success' : 'danger'}>
          {t(status.listening ? 'publicIpCertificate.listening' : 'publicIpCertificate.notListening', {
            port: status.challengePort,
          })}
        </Badge>
      </div>
      {status.listenError && <p className="text-sm text-danger">{status.listenError}</p>}
      <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-[auto_1fr]">
        <dt className="text-muted">{t('publicIpCertificate.validUntil')}</dt>
        <dd>{certificate ? formatDate(certificate.notAfter) : t('publicIpCertificate.none')}</dd>
        <dt className="text-muted">{t('publicIpCertificate.lastRenewal')}</dt>
        <dd>{status.lastRenewalAt ? formatDate(status.lastRenewalAt) : t('common.never')}</dd>
        {status.nextAttemptAt && (
          <>
            <dt className="text-muted">{t('publicIpCertificate.nextAttempt')}</dt>
            <dd>{formatDate(status.nextAttemptAt)}</dd>
          </>
        )}
      </dl>
      {status.lastError && (
        <Alert>
          {t('publicIpCertificate.lastError', {
            when: status.lastErrorAt ? formatDate(status.lastErrorAt) : '',
            error: status.lastError,
          })}
        </Alert>
      )}
      {renew.isError && <Alert>{errorMessage(t, renew.error)}</Alert>}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-xs text-muted">{t('publicIpCertificate.attemptsLeft', { count: status.attemptsLeft })}</p>
        <Button
          variant="secondary"
          busy={renew.isPending}
          disabled={status.renewing || status.attemptsLeft === 0}
          onClick={() => renew.mutate()}
        >
          <RefreshCw className="size-4" />
          {t('publicIpCertificate.renewNow')}
        </Button>
      </div>
    </div>
  );
}
