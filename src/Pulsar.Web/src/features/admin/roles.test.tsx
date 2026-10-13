import { QueryClient } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, problem, session } from '../../test/fakeApi';

const operator: Schemas['RoleSummary'] = {
  id: '0192f1a0-0000-7000-8000-0000000000c1',
  name: 'Operator',
  description: null,
  scope: 'server',
  isSystem: false,
  requiresTwoFactor: false,
  permissions: ['server.view', 'server.start'],
  assignedUsers: 0,
  createdAt: '2026-10-01T12:00:00Z',
  updatedAt: '2026-10-01T12:00:00Z',
};
const catalog: Schemas['PermissionResponse'][] = [
  { key: 'server.view', grantableScopes: ['global', 'node', 'server'] },
  { key: 'server.start', grantableScopes: ['global', 'node', 'server'] },
  { key: 'server.stop', grantableScopes: ['global', 'node', 'server'] },
];

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
  render(<App router={router} queryClient={queryClient} />);
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('editing a role', () => {
  it('cannot be saved before the permission catalog is loaded, which would strip the role of its permissions', async () => {
    const saved: unknown[] = [];
    let load: (() => void) | undefined;
    fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/roles': () => json([operator]),
      'GET /api/v1/permissions': () =>
        new Promise<Response>((resolve) => {
          load = () => resolve(json(catalog));
        }),
      [`PUT /api/v1/roles/${operator.id}`]: async (request) => {
        saved.push(await request.json());
        return json(operator);
      },
    });
    renderAt('/admin/roles');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    const save = within(dialog).getByRole('button', { name: 'Save' });
    expect(save).toBeDisabled();
    await user.type(within(dialog).getByLabelText('Name'), '{Enter}');
    expect(saved).toEqual([]);

    load?.();
    await within(dialog).findByRole('checkbox', { name: /View/i });
    expect(save).toBeEnabled();
    await user.click(save);

    await vi.waitFor(() =>
      expect(saved).toEqual([{ name: 'Operator', description: null, permissions: ['server.view', 'server.start'] }]),
    );
  });

  it('says the catalog could not be loaded, offers to try again and keeps Save off until it is', async () => {
    let failing = true;
    fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/roles': () => json([operator]),
      'GET /api/v1/permissions': () => (failing ? problem(500, 'unexpected') : json(catalog)),
    });
    renderAt('/admin/roles');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByRole('alert')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDisabled();

    failing = false;
    await user.click(within(dialog).getByRole('button', { name: 'Try again' }));

    await vi.waitFor(() => expect(within(dialog).getByRole('button', { name: 'Save' })).toBeEnabled());
    expect(within(dialog).queryByRole('alert')).not.toBeInTheDocument();
  });
});
