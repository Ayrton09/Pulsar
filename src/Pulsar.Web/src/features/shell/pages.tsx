import { useEffect, useState } from 'react';
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
 * (a tab opened before an update) reloads once on its own, if the panel answers: the same error comes from a browser
 * offline or a panel restarting, and then the page says so instead.
 */
export function RouteError() {
  const { t } = useTranslation();
  const staleBuild = isStaleBuildError(useRouteError());
  const [unreachable, setUnreachable] = useState(false);

  useEffect(() => {
    if (!staleBuild) {
      return;
    }
    let current = true;
    void reloadForNewBuild().then((result) => current && setUnreachable(result === 'unreachable'));
    return () => {
      current = false;
    };
  }, [staleBuild]);

  const [title, description] = !staleBuild
    ? [t('routeError.title'), t('routeError.description')]
    : unreachable
      ? [t('routeError.unreachableTitle'), t('routeError.unreachableDescription')]
      : [t('routeError.updatedTitle'), t('routeError.updatedDescription')];
  return (
    <FullScreenMessage>
      <div className="max-w-md space-y-4 text-center" role="alert">
        <h1 className="text-xl font-semibold">{title}</h1>
        <p className="text-muted">{description}</p>
        <Button variant="secondary" onClick={reloadPage}>
          {t('routeError.reload')}
        </Button>
      </div>
    </FullScreenMessage>
  );
}
