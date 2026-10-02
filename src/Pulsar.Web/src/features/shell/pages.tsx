import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';

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
