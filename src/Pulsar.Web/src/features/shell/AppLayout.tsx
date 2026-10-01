import { useTranslation } from 'react-i18next';
import { NavLink, Outlet } from 'react-router';
import { Button } from '../../components/ui/Button';
import { can, Permission } from '../../lib/permissions';
import { useCurrentUser, useLogout } from '../auth/api';
import { InsecureConnectionBanner } from '../auth/InsecureConnectionBanner';

interface NavItem {
  to: string;
  label: string;
  /** Shown only to users holding any of these; empty = everyone. */
  anyOf: string[];
}

function NavSection({ title, items }: { title?: string; items: NavItem[] }) {
  if (items.length === 0) {
    return null;
  }
  return (
    <div className="space-y-1">
      {title && <p className="px-3 pt-4 pb-1 text-xs font-medium tracking-wide text-muted uppercase">{title}</p>}
      {items.map((item) => (
        <NavLink
          key={item.to}
          to={item.to}
          end={item.to === '/'}
          className={({ isActive }) =>
            `block rounded-md px-3 py-2 text-sm ${isActive ? 'bg-surface-raised font-medium text-text' : 'text-muted hover:text-text'}`
          }
        >
          {item.label}
        </NavLink>
      ))}
    </div>
  );
}

/** Sidebar layout of the signed-in panel. Entries the user cannot use are not shown. */
export function AppLayout() {
  const { t } = useTranslation();
  const user = useCurrentUser();
  const logout = useLogout();

  const visible = (items: NavItem[]) => items.filter((item) => item.anyOf.length === 0 || can(user, ...item.anyOf));
  const main = visible([{ to: '/', label: t('nav.dashboard'), anyOf: [] }]);
  const admin = visible([
    { to: '/admin/users', label: t('nav.users'), anyOf: [Permission.UserView, Permission.UserManage] },
    {
      to: '/admin/roles',
      label: t('nav.roles'),
      anyOf: [Permission.RoleManage, Permission.UserView, Permission.UserManage],
    },
    { to: '/admin/audit', label: t('nav.audit'), anyOf: [Permission.AuditView] },
  ]);

  return (
    <div className="flex min-h-screen flex-col">
      <InsecureConnectionBanner />
      <div className="flex flex-1 flex-col md:flex-row">
        <aside className="flex flex-col border-b border-border bg-surface md:w-60 md:border-r md:border-b-0">
          <div className="px-6 py-5 text-lg font-semibold tracking-tight">Pulsar</div>
          <nav aria-label={t('nav.label')} className="flex-1 px-3 pb-4">
            <NavSection items={main} />
            <NavSection title={t('nav.administration')} items={admin} />
          </nav>
          <div className="border-t border-border px-6 py-4 text-sm">
            <NavLink to="/account" className="block rounded-md hover:text-accent">
              <span className="block truncate font-medium">{user.displayName ?? user.userName}</span>
              <span className="block truncate text-xs text-muted">{t('nav.account')}</span>
            </NavLink>
            <Button
              variant="secondary"
              size="sm"
              className="mt-3 w-full"
              busy={logout.isPending}
              onClick={() => logout.mutate()}
            >
              {t('actions.signOut')}
            </Button>
          </div>
        </aside>
        <main className="min-w-0 flex-1 p-6 md:p-10">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
