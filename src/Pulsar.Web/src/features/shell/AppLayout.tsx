import * as RadixDialog from '@radix-ui/react-dialog';
import {
  Archive,
  Bell,
  Database,
  Download,
  Gamepad2,
  HardDrive,
  KeyRound,
  LayoutDashboard,
  LogOut,
  Menu,
  ScrollText,
  Server,
  LogIn,
  ShieldCheck,
  Users,
  X,
  type LucideIcon,
} from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, NavLink, Outlet } from 'react-router';
import { Button } from '../../components/ui/Button';
import { Logo } from '../../components/ui/Logo';
import { errorMessage } from '../../lib/errors';
import { canAnywhere, Permission } from '../../lib/permissions';
import { useCurrentUser, useLogout } from '../auth/api';
import { InsecureConnectionBanner } from '../auth/InsecureConnectionBanner';
import { useLiveStatus } from '../live/hooks';
import { LiveProvider } from '../live/LiveProvider';
import { UpdateBanner } from '../updates/UpdateBanner';

interface NavItem {
  to: string;
  label: string;
  icon: LucideIcon;
  /** Shown only to users holding any of these; empty = everyone. */
  anyOf: string[];
}

// Tailwind's md breakpoint: from here on the sidebar is always shown and the menu panel is not needed.
const wideScreen = '(min-width: 48rem)';

function NavSection({
  title,
  items,
  onNavigate,
}: {
  title?: string;
  items: NavItem[];
  onNavigate?: (() => void) | undefined;
}) {
  if (items.length === 0) {
    return null;
  }
  return (
    <div className="space-y-0.5">
      {title && <p className="px-3 pt-5 pb-1.5 text-xs font-medium tracking-wide text-muted uppercase">{title}</p>}
      {items.map(({ to, label, icon: Icon }) => (
        <NavLink
          key={to}
          to={to}
          end={to === '/'}
          onClick={onNavigate}
          className={({ isActive }) =>
            `group relative flex items-center gap-3 rounded-lg px-3 py-2 text-sm transition-colors ${isActive ? 'bg-linear-to-r from-accent/20 to-accent/5 font-medium text-text before:absolute before:inset-y-1.5 before:left-0 before:w-0.5 before:rounded-full before:bg-accent' : 'text-muted hover:bg-surface-raised hover:text-text'}`
          }
        >
          <Icon className="size-4 shrink-0 group-aria-[current=page]:text-accent" />
          <span className="truncate">{label}</span>
        </NavLink>
      ))}
    </div>
  );
}

/** Says when live updates stopped: what the page shows may be getting old until the channel is back. */
function LiveStatusNote() {
  const { t } = useTranslation();
  const status = useLiveStatus();
  return status === 'reconnecting' ? (
    <p role="status" className="px-6 pb-3 text-xs text-danger">
      {t('live.reconnecting')}
    </p>
  ) : null;
}

function Brand({ onNavigate }: { onNavigate?: (() => void) | undefined }) {
  return (
    <Link to="/" onClick={onNavigate} className="rounded-md">
      <Logo />
    </Link>
  );
}

/** Up to two letters of the user's name, for the round badge next to it. */
function initials(name: string): string {
  const words = name.trim().split(/\s+/);
  const picked = words.length > 1 ? [words[0], words[words.length - 1]] : words;
  return picked
    .map((word) => Array.from(word ?? '')[0] ?? '')
    .join('')
    .toLocaleUpperCase();
}

/** Sidebar layout of the signed-in panel, with its live channel. Entries the user cannot use are not shown. */
export function AppLayout() {
  return (
    <LiveProvider>
      <Shell />
    </LiveProvider>
  );
}

/**
 * The brand, the navigation and the user's own links. Shown as the sidebar on wide screens and inside the menu panel
 * on small ones; choosing a page there closes the panel (onNavigate).
 */
function Sidebar({ onNavigate }: { onNavigate?: (() => void) | undefined }) {
  const { t } = useTranslation();
  const user = useCurrentUser();
  const logout = useLogout();
  const name = user.displayName ?? user.userName;

  const visible = (items: NavItem[]) =>
    items.filter((item) => item.anyOf.length === 0 || canAnywhere(user, ...item.anyOf));
  const main = visible([
    { to: '/', label: t('nav.dashboard'), icon: LayoutDashboard, anyOf: [] },
    { to: '/servers', label: t('nav.servers'), icon: Server, anyOf: [Permission.ServerView] },
    {
      to: '/nodes',
      label: t('nav.nodes'),
      icon: HardDrive,
      anyOf: [Permission.NodeView, Permission.NodeManage, Permission.NodeCredentialsManage, Permission.SettingsManage],
    },
    {
      to: '/templates',
      label: t('nav.templates'),
      icon: Gamepad2,
      anyOf: [Permission.TemplateView, Permission.TemplateManage],
    },
  ]);
  const admin = visible([
    { to: '/admin/users', label: t('nav.users'), icon: Users, anyOf: [Permission.UserView, Permission.UserManage] },
    {
      to: '/admin/roles',
      label: t('nav.roles'),
      icon: ShieldCheck,
      anyOf: [Permission.RoleManage, Permission.UserView, Permission.UserManage],
    },
    { to: '/admin/audit', label: t('nav.audit'), icon: ScrollText, anyOf: [Permission.AuditView] },
    { to: '/admin/notifications', label: t('nav.notifications'), icon: Bell, anyOf: [Permission.SettingsManage] },
    {
      to: '/admin/backup-storage',
      label: t('nav.backupStorage'),
      icon: Archive,
      anyOf: [Permission.SettingsManage],
    },
    {
      to: '/admin/database-hosts',
      label: t('nav.databaseHosts'),
      icon: Database,
      anyOf: [Permission.SettingsManage],
    },
    {
      to: '/admin/steam-accounts',
      label: t('nav.steamAccounts'),
      icon: KeyRound,
      anyOf: [Permission.SettingsManage],
    },
    { to: '/admin/sign-in', label: t('nav.signIn'), icon: LogIn, anyOf: [Permission.SettingsManage] },
    { to: '/admin/updates', label: t('nav.updates'), icon: Download, anyOf: [Permission.SettingsManage] },
  ]);

  return (
    <>
      <div className="px-5 py-5">
        <Brand onNavigate={onNavigate} />
      </div>
      <nav aria-label={t('nav.label')} className="flex-1 overflow-y-auto px-3 pb-4">
        <NavSection items={main} onNavigate={onNavigate} />
        <NavSection title={t('nav.administration')} items={admin} onNavigate={onNavigate} />
      </nav>
      <LiveStatusNote />
      <div className="border-t border-border p-3">
        <NavLink
          to="/account"
          onClick={onNavigate}
          className={({ isActive }) =>
            `flex items-center gap-3 rounded-md px-2 py-2 transition-colors hover:bg-surface-raised ${isActive ? 'bg-surface-raised' : ''}`
          }
        >
          <span
            aria-hidden="true"
            className="grid size-8 shrink-0 place-items-center rounded-full bg-accent/15 text-xs font-semibold text-accent"
          >
            {initials(name)}
          </span>
          <span className="min-w-0">
            <span className="block truncate text-sm font-medium">{name}</span>
            <span className="block truncate text-xs text-muted">{t('nav.account')}</span>
          </span>
        </NavLink>
        <Button
          variant="ghost"
          size="sm"
          icon={LogOut}
          className="mt-1 w-full justify-start text-muted hover:text-text"
          busy={logout.isPending}
          onClick={() => logout.mutate()}
        >
          {t('actions.signOut')}
        </Button>
        {logout.isError && (
          <p role="alert" className="mt-1 px-2 text-xs text-danger">
            {errorMessage(t, logout.error)}
          </p>
        )}
      </div>
    </>
  );
}

function Shell() {
  const { t } = useTranslation();
  const [menuOpen, setMenuOpen] = useState(false);

  // The panel belongs to small screens: one that grows past the breakpoint (a rotated tablet) closes it, so it does
  // not keep the page locked behind it with the sidebar already in view.
  useEffect(() => {
    const query = window.matchMedia(wideScreen);
    const close = (event: MediaQueryListEvent) => {
      if (event.matches) {
        setMenuOpen(false);
      }
    };
    query.addEventListener('change', close);
    return () => query.removeEventListener('change', close);
  }, []);

  return (
    <div className="min-h-dvh md:flex">
      <aside className="hidden border-r border-border bg-surface/70 backdrop-blur md:sticky md:top-0 md:flex md:h-dvh md:w-72 md:shrink-0 md:flex-col">
        <Sidebar />
      </aside>
      <RadixDialog.Root open={menuOpen} onOpenChange={setMenuOpen}>
        <div className="flex min-w-0 flex-1 flex-col">
          <header className="sticky top-0 z-30 flex h-14 items-center gap-3 border-b border-border bg-surface/95 px-4 backdrop-blur md:hidden">
            <RadixDialog.Trigger
              aria-label={t('nav.openMenu')}
              className="-ml-1 grid size-9 place-items-center rounded-md text-muted hover:bg-surface-raised hover:text-text focus-visible:outline-2 focus-visible:outline-accent"
            >
              <Menu className="size-5" />
            </RadixDialog.Trigger>
            <Brand />
          </header>
          <InsecureConnectionBanner />
          <UpdateBanner />
          <main className="flex-1 px-4 py-6 sm:px-6 md:px-10 md:py-10">
            <div className="mx-auto max-w-7xl">
              <Outlet />
            </div>
          </main>
        </div>
        <RadixDialog.Portal>
          <RadixDialog.Overlay className="fixed inset-0 z-40 bg-black/60 motion-safe:animate-[pulsar-fade-in_150ms_ease-out] md:hidden" />
          <RadixDialog.Content
            aria-describedby={undefined}
            className="fixed inset-y-0 left-0 z-50 flex w-72 max-w-[85vw] flex-col border-r border-border bg-surface shadow-xl motion-safe:animate-[pulsar-drawer-in_200ms_ease-out] md:hidden"
          >
            <RadixDialog.Title className="sr-only">{t('nav.menu')}</RadixDialog.Title>
            <RadixDialog.Close
              aria-label={t('actions.close')}
              className="absolute top-4 right-3 grid size-9 place-items-center rounded-md text-muted hover:bg-surface-raised hover:text-text focus-visible:outline-2 focus-visible:outline-accent"
            >
              <X className="size-5" />
            </RadixDialog.Close>
            <Sidebar onNavigate={() => setMenuOpen(false)} />
          </RadixDialog.Content>
        </RadixDialog.Portal>
      </RadixDialog.Root>
    </div>
  );
}
