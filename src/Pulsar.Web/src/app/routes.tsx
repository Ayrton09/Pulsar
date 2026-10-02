import type { RouteObject } from 'react-router';
import { AccountPage } from '../features/auth/AccountPage';
import { PublicOnly, RequirePermission, RequireSession } from '../features/auth/guards';
import { LoginPage } from '../features/auth/LoginPage';
import { SetupPage } from '../features/auth/SetupPage';
import { AppLayout } from '../features/shell/AppLayout';
import { DashboardPage, NotFoundPage } from '../features/shell/pages';
import { Permission } from '../lib/permissions';

/** Route table (docs/architecture/07). Pages appear here as their phase delivers them. */
export const routes: RouteObject[] = [
  { element: <PublicOnly page="login" />, children: [{ path: '/login', element: <LoginPage /> }] },
  { element: <PublicOnly page="setup" />, children: [{ path: '/setup', element: <SetupPage /> }] },
  {
    element: <RequireSession />,
    children: [
      {
        element: <AppLayout />,
        children: [
          { index: true, element: <DashboardPage /> },
          { path: 'account', element: <AccountPage /> },
          {
            path: 'servers',
            lazy: async () => {
              const { ServersPage } = await import('../features/servers/ServersPage');
              return {
                element: (
                  <RequirePermission anyOf={[Permission.ServerView]}>
                    <ServersPage />
                  </RequirePermission>
                ),
              };
            },
          },
          {
            path: 'servers/:id',
            lazy: async () => {
              const { ServerPage } = await import('../features/servers/ServerPage');
              return {
                element: (
                  <RequirePermission anyOf={[Permission.ServerView]}>
                    <ServerPage />
                  </RequirePermission>
                ),
              };
            },
          },
          {
            path: 'templates',
            lazy: async () => {
              const { TemplatesPage } = await import('../features/servers/TemplatesPage');
              return {
                element: (
                  <RequirePermission anyOf={[Permission.TemplateView, Permission.TemplateManage]}>
                    <TemplatesPage />
                  </RequirePermission>
                ),
              };
            },
          },
          {
            path: 'nodes',
            lazy: async () => {
              const { NodesPage } = await import('../features/nodes/NodesPage');
              return {
                element: (
                  <RequirePermission anyOf={[Permission.NodeView, Permission.NodeManage]}>
                    <NodesPage />
                  </RequirePermission>
                ),
              };
            },
          },
          {
            path: 'nodes/:id',
            lazy: async () => {
              const { NodePage } = await import('../features/nodes/NodePage');
              return {
                element: (
                  <RequirePermission anyOf={[Permission.NodeView, Permission.NodeManage]}>
                    <NodePage />
                  </RequirePermission>
                ),
              };
            },
          },
          // Administration pages load on demand: most sessions never open them.
          {
            path: 'admin/users',
            lazy: async () => {
              const { UsersPage } = await import('../features/admin/UsersPage');
              return {
                element: (
                  <RequirePermission anyOf={[Permission.UserView, Permission.UserManage]}>
                    <UsersPage />
                  </RequirePermission>
                ),
              };
            },
          },
          {
            path: 'admin/roles',
            lazy: async () => {
              const { RolesPage } = await import('../features/admin/RolesPage');
              return {
                element: (
                  <RequirePermission anyOf={[Permission.RoleManage, Permission.UserView, Permission.UserManage]}>
                    <RolesPage />
                  </RequirePermission>
                ),
              };
            },
          },
          {
            path: 'admin/audit',
            lazy: async () => {
              const { AuditPage } = await import('../features/admin/AuditPage');
              return {
                element: (
                  <RequirePermission anyOf={[Permission.AuditView]}>
                    <AuditPage />
                  </RequirePermission>
                ),
              };
            },
          },
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
];
