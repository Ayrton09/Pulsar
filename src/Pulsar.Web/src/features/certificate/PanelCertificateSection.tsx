import { useTranslation } from 'react-i18next';
import { Spinner } from '../../components/ui/Button';
import { Alert, Card } from '../../components/ui/Display';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { CopyButton } from '../sftp/CopyButton';
import { formatFingerprint, panelCertificateDownloadPath, usePanelCertificate, type PanelCertificate } from './api';

/**
 * The panel's own HTTPS certificate on the account page (docs/guides/install.md, "Trusting the panel's certificate"):
 * its fingerprint, what it lists, and, for a self-signed one like the installer's, the file and the steps to trust it
 * once in this computer's browsers, after checking the file against the fingerprint the installer showed.
 */
export function PanelCertificateSection() {
  const { t } = useTranslation();
  const certificate = usePanelCertificate();
  const data = certificate.data;

  return (
    <Card className="space-y-4 p-6">
      <div>
        <h2 className="text-lg font-semibold">{t('panelCertificate.title')}</h2>
        <p className="mt-1 text-sm text-muted">{t('panelCertificate.hint')}</p>
      </div>
      {certificate.isPending && <Spinner label={t('common.loading')} />}
      {certificate.isError && <Alert>{errorMessage(t, certificate.error)}</Alert>}
      {data === null && <p className="text-sm text-muted">{t('panelCertificate.unavailable')}</p>}
      {data && <Details certificate={data} />}
    </Card>
  );
}

function Details({ certificate }: { certificate: PanelCertificate }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const fingerprint = formatFingerprint(certificate.sha256);
  const names = [...certificate.dnsNames, ...certificate.ipAddresses];
  const label = t('panelCertificate.fingerprint');

  return (
    <>
      <div>
        <p className="mb-1 text-sm font-medium">{label}</p>
        <div className="flex flex-wrap items-center gap-2">
          <code className="min-w-0 rounded-md border border-border px-2 py-1.5 font-mono text-xs break-all">
            {fingerprint}
          </code>
          <CopyButton text={fingerprint} label={t('panelCertificate.copy', { what: label })} />
        </div>
      </div>
      <p className="text-sm">
        {names.length > 0
          ? t('panelCertificate.covers', { names: names.join(', '), date: formatDate(certificate.notAfter) })
          : t('panelCertificate.validUntil', { date: formatDate(certificate.notAfter) })}
      </p>
      {certificate.trustable ? (
        <TrustSteps />
      ) : (
        <p className="text-sm text-muted">
          {certificate.selfSigned ? t('panelCertificate.notTrustable') : t('panelCertificate.issued')}
        </p>
      )}
    </>
  );
}

function TrustSteps() {
  const { t } = useTranslation();
  return (
    <>
      <div role="note" className="rounded-md border border-warning/40 bg-warning/10 px-3 py-2 text-sm">
        <p className="font-semibold">{t('panelCertificate.checkTitle')}</p>
        <p className="mt-1">{t('panelCertificate.check')}</p>
      </div>
      <a
        href={panelCertificateDownloadPath}
        download
        className="inline-flex h-8 items-center rounded-md border border-border bg-surface px-3 text-sm font-medium hover:bg-surface-raised focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
      >
        {t('panelCertificate.download')}
      </a>
      <div className="space-y-1 text-sm">
        <p className="font-medium">{t('panelCertificate.windowsTitle')}</p>
        <ol className="list-decimal space-y-1 pl-5">
          <li>{t('panelCertificate.windows1')}</li>
          <li>{t('panelCertificate.windows2')}</li>
          <li>{t('panelCertificate.windows3')}</li>
          <li>{t('panelCertificate.windows4')}</li>
          <li>{t('panelCertificate.windows5')}</li>
        </ol>
      </div>
      <p className="text-sm">{t('panelCertificate.firefox')}</p>
      <p className="text-xs text-muted">{t('panelCertificate.after')}</p>
    </>
  );
}
