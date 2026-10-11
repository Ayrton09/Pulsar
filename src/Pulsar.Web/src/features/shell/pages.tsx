import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useRouteError } from 'react-router';
import { Button } from '../../components/ui/Button';
import { isStaleBuildError, reloadForNewBuild, reloadPage } from '../../lib/reload';
import { FullScreenMessage } from '../auth/guards';

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

/**
 * A page failed to load or to render (the root route's error element). A page of a build the panel no longer serves
 * (a tab opened before an update) reloads once on its own.
 */
export function RouteError() {
  const { t } = useTranslation();
  const staleBuild = isStaleBuildError(useRouteError());

  useEffect(() => {
    if (staleBuild) {
      reloadForNewBuild();
    }
  }, [staleBuild]);

  return (
    <FullScreenMessage>
      <div className="max-w-md space-y-4 text-center" role="alert">
        <h1 className="text-xl font-semibold">{staleBuild ? t('routeError.updatedTitle') : t('routeError.title')}</h1>
        <p className="text-muted">{staleBuild ? t('routeError.updatedDescription') : t('routeError.description')}</p>
        <Button variant="secondary" onClick={reloadPage}>
          {t('routeError.reload')}
        </Button>
      </div>
    </FullScreenMessage>
  );
}
