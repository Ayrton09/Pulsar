import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Navigate, Outlet, useLocation, useSearchParams } from 'react-router';
import { Button, Spinner } from '../../components/ui/Button';
import { errorMessage } from '../../lib/errors';
import { canAnywhere } from '../../lib/permissions';
import { useLogout, useSession, useSessionRefresh } from './api';
import { AuthCard } from './AuthCard';
import { safeReturnTo } from './returnTo';
import { TwoFactorSetup } from './TwoFactor';

export function FullScreenMessage({ children }: { children: ReactNode }) {
  return <main className="flex min-h-screen items-center justify-center p-6">{children}</main>;
}

function SessionError({ error, retry }: { error: unknown; retry: () => void }) {
  const { t } = useTranslation();
  return (
    <FullScreenMessage>
      <div className="space-y-4 text-center" role="alert">
        <p>{errorMessage(t, error)}</p>
        <Button variant="secondary" onClick={retry}>
          {t('actions.retry')}
        </Button>
      </div>
    </FullScreenMessage>
  );
}

function Loading() {
  const { t } = useTranslation();
  return (
    <FullScreenMessage>
      <Spinner label={t('common.loading')} />
    </FullScreenMessage>
  );
}

/** Pages for signed-in users. Sends everyone else to the setup or login page. */
export function RequireSession() {
  const session = useSession();
  const location = useLocation();
  if (session.isPending) {
    return <Loading />;
  }
  if (session.isError) {
    return <SessionError error={session.error} retry={() => void session.refetch()} />;
  }
  if (session.data.setupRequired) {
    return <Navigate to="/setup" replace />;
  }
  if (!session.data.user) {
    const returnTo = location.pathname + location.search;
    return <Navigate to={returnTo === '/' ? '/login' : `/login?returnTo=${encodeURIComponent(returnTo)}`} replace />;
  }
  if (session.data.user.twoFactorSetupRequired) {
    return <TwoFactorRequired />;
  }
  return <Outlet />;
}

/** A role requires two-factor authentication: nothing else is open until the user sets it up (the API agrees). */
function TwoFactorRequired() {
  const { t } = useTranslation();
  const refresh = useSessionRefresh();
  const logout = useLogout();
  return (
    <AuthCard title={t('twoFactor.requiredTitle')} subtitle={t('twoFactor.requiredSubtitle')}>
      <TwoFactorSetup onDone={() => void refresh()} />
      <div className="mt-6 border-t border-border pt-4 text-right">
        <Button variant="ghost" size="sm" onClick={() => logout.mutate()} busy={logout.isPending}>
          {t('actions.signOut')}
        </Button>
      </div>
    </AuthCard>
  );
}

/** The login and setup pages: only reachable when they make sense. */
export function PublicOnly({ page }: { page: 'login' | 'setup' }) {
  const session = useSession();
  const [params] = useSearchParams();
  if (session.isPending) {
    return <Loading />;
  }
  if (session.isError) {
    return <SessionError error={session.error} retry={() => void session.refetch()} />;
  }
  if (session.data.user) {
    // Signing in reloads the session; this is what takes the user where they were going.
    return <Navigate to={safeReturnTo(params.get('returnTo'))} replace />;
  }
  if (page === 'login' && session.data.setupRequired) {
    return <Navigate to="/setup" replace />;
  }
  if (page === 'setup' && !session.data.setupRequired) {
    return <Navigate to="/login" replace />;
  }
  return <Outlet />;
}

/** Hides a page from users without any of the permissions (the API refuses them anyway). */
export function RequirePermission({ anyOf, children }: { anyOf: string[]; children: ReactNode }) {
  const { t } = useTranslation();
  const { data } = useSession();
  if (!canAnywhere(data?.user, ...anyOf)) {
    return (
      <div className="py-16 text-center" role="alert">
        <h1 className="text-xl font-semibold">{t('forbidden.title')}</h1>
        <p className="mt-2 text-muted">{t('forbidden.description')}</p>
      </div>
    );
  }
  return children;
}
