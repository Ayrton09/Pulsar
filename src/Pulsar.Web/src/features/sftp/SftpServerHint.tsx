import { useTranslation } from 'react-i18next';
import { sftpUrl, useSftpConnection } from './api';
import { CopyButton } from './CopyButton';

/** A small hint in a server's files: the SFTP address whose root is this server's folder. Nothing while SFTP is off. */
export function SftpServerHint({ shortId }: { shortId: string }) {
  const { t } = useTranslation();
  const connection = useSftpConnection();
  const data = connection.data;
  if (!data?.enabled) {
    return null;
  }

  const url = sftpUrl(`${data.userName}.${shortId}`, data.host, data.port);
  return (
    <div className="mt-4 flex flex-wrap items-center gap-2 text-sm">
      <span className="text-muted">{t('sftp.serverHint')}</span>
      <code className="min-w-0 font-mono text-xs break-all">{url}</code>
      <CopyButton text={url} label={t('sftp.copyServer')} />
    </div>
  );
}
