import { QueryClient } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, expect, it, vi } from 'vitest';
import { App } from '../../app/App';
import { routes } from '../../app/routes';
import { initI18n } from '../../app/i18n';
import { adminUser, fakeApi, json, session } from '../../test/fakeApi';
import type { AuditEntry } from './api';
import { auditCsv } from './auditExport';

const entry: AuditEntry = {
  id: '0192f1a0-0000-7000-8000-000000000001',
  occurredAt: '2026-10-02T12:00:00Z',
  action: 'server.renamed',
  outcome: 'success',
  actorType: 'user',
  actorUserId: adminUser.id,
  actorDisplay: 'Operator',
  targetType: 'server',
  targetId: 'example',
  ipAddress: '127.0.0.1',
  metadata: {},
};

beforeAll(async () => {
  await initI18n('en-US');
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

it('resets pagination when applied filters change and exports only the filtered bounded page', async () => {
  const requests: URL[] = [];
  fakeApi({
    'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['audit.view'] }),
    'GET /api/v1/audit': (request) => {
      const url = new URL(request.url);
      requests.push(url);
      const action = url.searchParams.get('action');
      if (action === 'role.')
        return json({ entries: [{ ...entry, action: 'role.created', id: 'role-entry' }], nextBefore: null });
      return json({
        entries: [{ ...entry, id: url.searchParams.get('before') ?? entry.id }],
        nextBefore: url.searchParams.has('before') ? null : 'next-cursor',
      });
    },
  });
  const from = '2026-10-01T12:00:00.000Z';
  const router = createMemoryRouter(routes, {
    initialEntries: [`/admin/audit?action=server.&from=${encodeURIComponent(from)}`],
  });
  render(<App router={router} queryClient={new QueryClient({ defaultOptions: { queries: { retry: false } } })} />);
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'Load more' }));
  await waitFor(() => expect(requests.some((url) => url.searchParams.get('before') === 'next-cursor')).toBe(true));
  expect(new Date((screen.getByLabelText('From (local time)') as HTMLInputElement).value).toISOString()).toBe(from);
  const action = screen.getByRole('textbox', { name: 'Action prefix' });
  await user.clear(action);
  await user.type(action, 'role.');
  await user.type(screen.getByRole('textbox', { name: 'Actor user ID' }), adminUser.id);
  await user.type(screen.getByRole('textbox', { name: 'Resource type' }), 'server');
  await user.click(screen.getByRole('button', { name: 'Apply filters' }));
  await screen.findByText('role.created');
  expect(screen.queryByText('server.renamed')).not.toBeInTheDocument();
  const filtered = requests.filter((url) => url.searchParams.get('action') === 'role.');
  expect(filtered).toHaveLength(1);
  expect(filtered[0]?.searchParams.has('before')).toBe(false);
  expect(router.state.location.search).toContain('actorUserId=');
  expect(new URLSearchParams(router.state.location.search).get('from')).toBe(from);
  const create = vi.fn(() => 'blob:audit');
  const revoke = vi.fn();
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: create, revokeObjectURL: revoke }));
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
  await user.click(screen.getByRole('button', { name: 'Export CSV' }));
  await waitFor(() => expect(create).toHaveBeenCalledOnce());
  const exported = requests.at(-1);
  expect(exported?.searchParams.get('limit')).toBe('200');
  expect(exported?.searchParams.get('action')).toBe('role.');
  expect(exported?.searchParams.get('actorUserId')).toBe(adminUser.id);
  expect(exported?.searchParams.get('from')).toBe(from);
  expect(exported?.searchParams.has('before')).toBe(false);
  expect(revoke).toHaveBeenCalledWith('blob:audit');
});

it('quotes exported cells and neutralizes spreadsheet formulas without including metadata', () => {
  const csv = auditCsv([{ ...entry, actorDisplay: ' =1+1', targetId: 'a,"b\nline', metadata: { private: 'omitted' } }]);
  expect(csv).toContain('"\' =1+1"');
  expect(csv).toContain('"a,""b\nline"');
  expect(csv).not.toContain('omitted');
});
