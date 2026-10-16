import { useTranslation } from 'react-i18next';
import { Spinner } from '../../components/ui/Button';
import { Alert, Card } from '../../components/ui/Display';
import { errorMessage } from '../../lib/errors';
import { sftpUrl, useSftpConnection } from './api';
import { CopyButton } from './CopyButton';

/**
 * How the signed-in user reaches the servers' files with an SFTP client (docs/guides/sftp.md): the address, and the
 * host key to compare at the first connection.
 */
export function SftpAccessSection() {
  const { t } = useTranslation();
  const connection = useSftpConnection();
  const data = connection.data;

  return (
    <Card className="space-y-4 p-6">
      <div>
        <h2 className="text-lg font-semibold">{t('sftp.title')}</h2>
        <p className="mt-1 text-sm text-muted">{t('sftp.hint')}</p>
      </div>
      {connection.isPending && <Spinner label={t('common.loading')} />}
      {connection.isError && <Alert>{errorMessage(t, connection.error)}</Alert>}
      {data && !data.enabled && <p className="text-sm text-muted">{t('sftp.disabled')}</p>}
      {data?.enabled && (
        <>
          <ValueRow label={t('sftp.address')} value={sftpUrl(data.userName, data.host, data.port)} />
          <div className="space-y-1">
            <ValueRow label={t('sftp.fingerprint')} value={data.hostKeyFingerprint} />
            <p className="text-xs text-muted">{t('sftp.fingerprintHint')}</p>
          </div>
          <ul className="list-disc space-y-1 pl-5 text-xs text-muted">
            <li>{t('sftp.twoFactor')}</li>
            <li>{t('sftp.serverRoot')}</li>
          </ul>
        </>
      )}
    </Card>
  );
}

function ValueRow({ label, value }: { label: string; value: string }) {
  const { t } = useTranslation();
  return (
    <div>
      <p className="mb-1 text-sm font-medium">{label}</p>
      <div className="flex flex-wrap items-center gap-2">
        <code className="min-w-0 rounded-md border border-border px-2 py-1.5 font-mono text-xs break-all">{value}</code>
        <CopyButton text={value} label={t('sftp.copy', { what: label })} />
      </div>
    </div>
  );
}
