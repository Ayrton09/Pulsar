import { useTranslation } from 'react-i18next';
import { useSession } from './api';

/** Shown while the panel is used over plain HTTP (allowed by the administrator with Security:AllowInsecureHttp). */
export function InsecureConnectionBanner() {
  const { t } = useTranslation();
  const { data } = useSession();
  if (!data?.insecureConnection) {
    return null;
  }
  return (
    <div role="status" className="border-b border-danger/40 bg-danger/10 px-4 py-2 text-center text-sm text-danger">
      <strong className="font-semibold">{t('security.insecureTitle')}</strong> {t('security.insecureDescription')}
    </div>
  );
}
