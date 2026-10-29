import { QueryClient } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, problem, session } from '../../test/fakeApi';

const settingsManager = { ...adminUser, permissions: [...adminUser.permissions, 'settings.manage', 'server.view'] };
const channelId = '0192f1a0-0000-7000-8000-0000000000b1';

const discord: Schemas['NotificationChannelView'] = {
  id: channelId,
  name: 'Ops on Discord',
  kind: 'discord',
  enabled: true,
  events: ['serverCrashed', 'nodeOffline'],
  serverIds: null,
  language: 'es',
  recipients: [],
  webhook: '123456789',
  createdAt: '2026-10-04T12:00:00Z',
  updatedAt: '2026-10-04T12:00:00Z',
};

const settings: Schemas['NotificationSettingsView'] = {
  panelUrl: null,
  diskLowPercent: 10,
  nodeOfflineMinutes: 3,
  cpuHighPercent: 90,
  memoryHighPercent: 90,
  resourceHighMinutes: 10,
  smtp: null,
};

const deliveries: Schemas['NotificationDeliveryList'] = {
  deliveries: [
    {
      id: '0192f1a0-0000-7000-8000-0000000000c1',
      channelId,
      channelName: 'Ops on Discord',
      event: 'serverCrashed',
      title: 'Retakes se cayó',
      status: 'failed',
      attempts: 1,
      lastError: 'The webhook does not exist any more (404).',
      createdAt: '2026-10-04T12:05:00Z',
      sentAt: null,
      nextAttemptAt: null,
    },
  ],
};

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
  render(<App router={router} queryClient={queryClient} />);
  return router;
}

function api(extra: Record<string, (request: Request) => Response | Promise<Response>> = {}) {
  return fakeApi({
    'GET /api/v1/auth/session': session(settingsManager),
    'GET /api/v1/notifications/channels': () => json({ channels: [discord] }),
    'GET /api/v1/notifications/settings': () => json(settings),
    'GET /api/v1/notifications/deliveries': () => json(deliveries),
    'GET /api/v1/servers': () => json([]),
    ...extra,
  });
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('notifications page', () => {
  it('lists channels, settings and recent messages for settings managers', async () => {
    api();
    renderAt('/admin/notifications');

    const table = await screen.findByRole('table', { name: 'Channels' });
    expect(within(table).getByText('Ops on Discord')).toBeInTheDocument();
    expect(table).toHaveTextContent('Webhook 123456789 · Español');
    expect(table).toHaveTextContent('Server crashed');
    expect(table).toHaveTextContent('All servers');
    expect(screen.getByText('Email is off until an SMTP server is set.')).toBeInTheDocument();
    const messages = await screen.findByRole('table', { name: 'Recent messages' });
    expect(messages).toHaveTextContent('Retakes se cayó');
    expect(messages).toHaveTextContent('Failed');
    expect(messages).toHaveTextContent('The webhook does not exist any more (404).');
    expect(screen.getByRole('link', { name: 'Notifications' })).toBeInTheDocument();
  });

  it('is not offered without settings.manage', async () => {
    fakeApi({ 'GET /api/v1/auth/session': session(adminUser) });
    renderAt('/admin/notifications');

    expect(await screen.findByRole('heading', { name: 'Not available' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Notifications' })).not.toBeInTheDocument();
  });

  it('creates an email channel for some events and says why the server refused one', async () => {
    let created: Schemas['NotificationChannelRequest'] | null = null;
    let attempts = 0;
    api({
      'POST /api/v1/notifications/channels': async (request) => {
        attempts++;
        if (attempts === 1) {
          return problem(400, 'notifications.recipients_invalid');
        }
        created = (await request.json()) as Schemas['NotificationChannelRequest'];
        return json({ ...discord, ...created, id: 'new', kind: 'email', webhook: null }, 201);
      },
    });
    const user = userEvent.setup();
    renderAt('/admin/notifications');

    await user.click(await screen.findByRole('button', { name: 'Add channel' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add channel' });
    await user.type(within(dialog).getByLabelText('Name'), 'Admins');
    await user.selectOptions(within(dialog).getByLabelText('Type'), 'email');
    await user.type(within(dialog).getByLabelText('Email addresses'), 'not-an-address');
    expect(within(dialog).getByText('Email channels need the SMTP server in the settings below.')).toBeInTheDocument();
    await user.click(within(dialog).getByRole('checkbox', { name: 'Node offline (and back online)' }));
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await within(dialog).findByText('Write 1 to 10 valid email addresses.')).toBeInTheDocument();

    await user.clear(within(dialog).getByLabelText('Email addresses'));
    await user.type(within(dialog).getByLabelText('Email addresses'), 'ops@example.com, oncall@example.com');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(created).toEqual({
      name: 'Admins',
      kind: 'email',
      enabled: true,
      events: ['serverCrashed', 'serverStartFailed', 'backupFailed'],
      serverIds: null,
      language: 'en',
      webhookUrl: null,
      recipients: ['ops@example.com', 'oncall@example.com'],
    });
  });

  it('sends a test and shows what happened', async () => {
    api({
      [`POST /api/v1/notifications/channels/${channelId}/test`]: () =>
        json({ sent: false, error: 'The webhook does not exist any more (404).' }),
    });
    const user = userEvent.setup();
    renderAt('/admin/notifications');

    await user.click(await screen.findByRole('button', { name: 'Send test' }));

    expect(
      await screen.findByText('The test to Ops on Discord failed: The webhook does not exist any more (404).'),
    ).toBeInTheDocument();
  });

  it('saves SMTP settings without showing or resending the password', async () => {
    let saved: Schemas['NotificationSettingsRequest'] | null = null;
    api({
      'PUT /api/v1/notifications/settings': async (request) => {
        saved = (await request.json()) as Schemas['NotificationSettingsRequest'];
        return json({
          ...settings,
          smtp: {
            host: 'smtp.example.com',
            port: 465,
            security: 'tls',
            userName: 'pulsar',
            hasPassword: true,
            fromAddress: 'pulsar@example.com',
            fromName: null,
          },
        });
      },
    });
    const user = userEvent.setup();
    renderAt('/admin/notifications');

    await user.type(await screen.findByLabelText('SMTP server'), 'smtp.example.com');
    await user.clear(screen.getByLabelText('Port'));
    await user.type(screen.getByLabelText('Port'), '465');
    await user.selectOptions(screen.getByLabelText('Security'), 'tls');
    await user.type(screen.getByLabelText('User name'), 'pulsar');
    await user.type(screen.getByLabelText('Password'), 'app password');
    await user.type(screen.getByLabelText('From address'), 'pulsar@example.com');
    await user.clear(screen.getByLabelText('High CPU at (%)'));
    await user.type(screen.getByLabelText('High CPU at (%)'), '80');
    await user.clear(screen.getByLabelText('For at least (minutes)'));
    await user.type(screen.getByLabelText('For at least (minutes)'), '15');
    await user.click(screen.getByRole('button', { name: 'Save settings' }));

    expect(await screen.findByText('Settings saved.')).toBeInTheDocument();
    expect(saved).toMatchObject({
      smtpHost: 'smtp.example.com',
      smtpPort: 465,
      smtpSecurity: 'tls',
      smtpUserName: 'pulsar',
      smtpPassword: 'app password',
      clearSmtpPassword: false,
      fromAddress: 'pulsar@example.com',
      panelUrl: null,
      cpuHighPercent: 80,
      memoryHighPercent: 90,
      resourceHighMinutes: 15,
    });
    // The password field is emptied, with a hint that the saved one is kept.
    expect(screen.getByLabelText('Password')).toHaveValue('');
    expect(
      screen.getByText('Leave it empty to keep the saved password (only while the server and user stay the same).'),
    ).toBeInTheDocument();
  });
});
