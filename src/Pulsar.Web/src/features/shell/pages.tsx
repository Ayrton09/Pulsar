import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Card, PageHeader } from '../../components/ui/Display';
import { useCurrentUser } from '../auth/api';

export function DashboardPage() {
  const { t } = useTranslation();
  const user = useCurrentUser();
  return (
    <>
      <PageHeader
        title={t('dashboard.title')}
        description={t('dashboard.welcome', { name: user.displayName ?? user.userName })}
      />
      <Card className="p-6">
        <p className="text-sm text-muted">{t('dashboard.nextSteps')}</p>
      </Card>
    </>
  );
}

export function NotFoundPage() {
  const { t } = useTranslation();
  return (
    <div className="py-16 text-center">
      <h1 className="text-xl font-semibold">{t('notFound.title')}</h1>
      <p className="mt-2 text-muted">{t('notFound.description')}</p>
      <Link to="/" className="mt-6 inline-block text-sm text-accent hover:underline">
        {t('notFound.home')}
      </Link>
    </div>
  );
}
