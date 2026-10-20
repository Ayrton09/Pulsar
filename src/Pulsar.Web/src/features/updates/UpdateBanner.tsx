import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { can, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useUpdates } from './api';

/** Tells those who can update (settings.manage) that a newer release of Pulsar is available, on every page. */
export function UpdateBanner() {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const allowed = can(me, Permission.SettingsManage);
  const { data } = useUpdates(allowed);
  if (!allowed || !data?.available) {
    return null;
  }
  return (
    <div role="status" className="border-b border-accent/40 bg-accent/10 px-4 py-2 text-center text-sm">
      {t('updates.banner', { version: data.available.version })}{' '}
      <Link to="/admin/updates" className="font-semibold text-accent underline-offset-2 hover:underline">
        {t('updates.bannerLink')}
      </Link>
    </div>
  );
}
