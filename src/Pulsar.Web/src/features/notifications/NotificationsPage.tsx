import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Badge, Card, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { Checkbox, Field, Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { useServers } from '../servers/api';
import {
  notificationEvents,
  useCreateChannel,
  useDeleteChannel,
  useNotificationChannels,
  useNotificationDeliveries,
  useNotificationSettings,
  useTestChannel,
  useUpdateChannel,
  useUpdateNotificationSettings,
  type NotificationChannel,
  type NotificationEvent,
  type NotificationKind,
  type NotificationSettings,
  type SmtpSecurity,
} from './api';

/** Notification channels, settings and recent messages (docs/architecture/07, "Notifications"); settings managers only. */
export function NotificationsPage() {
  const { t } = useTranslation();
  const channels = useNotificationChannels();
  const settings = useNotificationSettings();
  const servers = useServers();
  const test = useTestChannel();
  const [editing, setEditing] = useState<NotificationChannel | 'new' | null>(null);
  const [tested, setTested] = useState<{ name: string; sent: boolean; error?: string | null } | null>(null);
  const serverNames = new Map((servers.data ?? []).map((s) => [s.id, s.name]));

  return (
    <>
      <PageHeader
        title={t('notifications.title')}
        description={t('notifications.description')}
        actions={<Button onClick={() => setEditing('new')}>{t('notifications.addChannel')}</Button>}
      />
      <section aria-labelledby="channels-title" className="space-y-3">
        <h2 id="channels-title" className="text-lg font-semibold">
          {t('notifications.channels')}
        </h2>
        {tested && (
          <div role="status">
            {tested.sent ? (
              <p className="text-sm text-success">{t('notifications.testSent', { name: tested.name })}</p>
            ) : (
              <Alert>{t('notifications.testFailed', { name: tested.name, error: tested.error })}</Alert>
            )}
          </div>
        )}
        {test.isError && <Alert>{errorMessage(t, test.error)}</Alert>}
        {channels.isPending && <Spinner label={t('common.loading')} />}
        {channels.isError && <Alert>{errorMessage(t, channels.error)}</Alert>}
        {channels.data && channels.data.channels.length === 0 && (
          <p className="text-sm text-muted">{t('notifications.noChannels')}</p>
        )}
        {channels.data && channels.data.channels.length > 0 && (
          <Table label={t('notifications.channels')}>
            <thead>
              <tr>
                <Th>{t('notifications.name')}</Th>
                <Th>{t('notifications.events')}</Th>
                <Th>{t('notifications.servers')}</Th>
                <Th>
                  <span className="sr-only">{t('common.actions')}</span>
                </Th>
              </tr>
            </thead>
            <tbody>
              {channels.data.channels.map((channel) => (
                <tr key={channel.id}>
                  <Td>
                    <div className="flex flex-wrap items-center gap-2 font-medium">
                      {channel.name}
                      <Badge tone="accent">{t(`notifications.kinds.${channel.kind}`)}</Badge>
                      {!channel.enabled && <Badge>{t('notifications.off')}</Badge>}
                    </div>
                    <p className="mt-0.5 text-xs text-muted">
                      {channel.kind === 'discord'
                        ? t('notifications.webhook', { id: channel.webhook ?? '?' })
                        : channel.recipients.join(', ')}
                      {' · '}
                      {t(`notifications.languages.${channel.language}`)}
                    </p>
                  </Td>
                  <Td>
                    <ul className="space-y-0.5 text-sm">
                      {channel.events.map((event) => (
                        <li key={event}>{t(`notifications.eventNames.${event}`)}</li>
                      ))}
                    </ul>
                  </Td>
                  <Td>
                    {channel.serverIds
                      ? channel.serverIds.map((id) => serverNames.get(id) ?? id).join(', ')
                      : t('notifications.allServers')}
                  </Td>
                  <Td className="whitespace-nowrap text-right">
                    <Button
                      variant="ghost"
                      size="sm"
                      disabled={test.isPending}
                      onClick={() => {
                        setTested(null);
                        test.mutate(channel.id, {
                          onSuccess: (result) =>
                            setTested({ name: channel.name, sent: result.sent, error: result.error }),
                        });
                      }}
                    >
                      {t('notifications.test')}
                    </Button>
                    <Button variant="ghost" size="sm" onClick={() => setEditing(channel)}>
                      {t('actions.edit')}
                    </Button>
                  </Td>
                </tr>
              ))}
            </tbody>
          </Table>
        )}
      </section>

      <section aria-labelledby="notification-settings-title" className="mt-8 space-y-3">
        <h2 id="notification-settings-title" className="text-lg font-semibold">
          {t('notifications.settings')}
        </h2>
        {settings.isPending && <Spinner label={t('common.loading')} />}
        {settings.isError && <Alert>{errorMessage(t, settings.error)}</Alert>}
        {settings.data && <SettingsForm settings={settings.data} />}
      </section>

      <Deliveries />

      {editing && (
        <ChannelDialog
          channel={editing === 'new' ? null : editing}
          emailConfigured={settings.data?.smtp != null}
          onClose={() => setEditing(null)}
        />
      )}
    </>
  );
}

function Deliveries() {
  const { t } = useTranslation();
  const deliveries = useNotificationDeliveries();
  const formatDate = useFormatDate();
  const tones = { sent: 'success', pending: 'neutral', failed: 'danger' } as const;
  return (
    <section aria-labelledby="deliveries-title" className="mt-8 space-y-3">
      <h2 id="deliveries-title" className="text-lg font-semibold">
        {t('notifications.deliveries')}
      </h2>
      {deliveries.isPending && <Spinner label={t('common.loading')} />}
      {deliveries.isError && <Alert>{errorMessage(t, deliveries.error)}</Alert>}
      {deliveries.data && deliveries.data.deliveries.length === 0 && (
        <p className="text-sm text-muted">{t('notifications.noDeliveries')}</p>
      )}
      {deliveries.data && deliveries.data.deliveries.length > 0 && (
        <Table label={t('notifications.deliveries')}>
          <thead>
            <tr>
              <Th>{t('notifications.when')}</Th>
              <Th>{t('notifications.channel')}</Th>
              <Th>{t('notifications.message')}</Th>
              <Th>{t('notifications.status')}</Th>
            </tr>
          </thead>
          <tbody>
            {deliveries.data.deliveries.map((delivery) => (
              <tr key={delivery.id}>
                <Td className="whitespace-nowrap">{formatDate(delivery.createdAt)}</Td>
                <Td>{delivery.channelName}</Td>
                <Td>{delivery.title}</Td>
                <Td>
                  <Badge tone={tones[delivery.status]}>{t(`notifications.statuses.${delivery.status}`)}</Badge>
                  {delivery.attempts > 1 && (
                    <span className="ml-2 text-xs text-muted">
                      {t('notifications.attempts')}: {delivery.attempts}
                    </span>
                  )}
                  {delivery.lastError && <p className="mt-1 text-xs text-danger">{delivery.lastError}</p>}
                  {delivery.nextAttemptAt && (
                    <p className="mt-1 text-xs text-muted">
                      {t('notifications.nextTry', { when: formatDate(delivery.nextAttemptAt) })}
                    </p>
                  )}
                </Td>
              </tr>
            ))}
          </tbody>
        </Table>
      )}
    </section>
  );
}

const securities: SmtpSecurity[] = ['startTls', 'tls', 'none'];

function SettingsForm({ settings }: { settings: NotificationSettings }) {
  const { t } = useTranslation();
  const save = useUpdateNotificationSettings();
  const [panelUrl, setPanelUrl] = useState(settings.panelUrl ?? '');
  const [diskLow, setDiskLow] = useState(String(settings.diskLowPercent));
  const [offline, setOffline] = useState(String(settings.nodeOfflineMinutes));
  const [host, setHost] = useState(settings.smtp?.host ?? '');
  const [port, setPort] = useState(String(settings.smtp?.port ?? 587));
  const [security, setSecurity] = useState<SmtpSecurity>(settings.smtp?.security ?? 'startTls');
  const [user, setUser] = useState(settings.smtp?.userName ?? '');
  const [password, setPassword] = useState('');
  const [clearPassword, setClearPassword] = useState(false);
  const [fromAddress, setFromAddress] = useState(settings.smtp?.fromAddress ?? '');
  const [fromName, setFromName] = useState(settings.smtp?.fromName ?? '');
  const [saved, setSaved] = useState(false);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    setSaved(false);
    save.mutate(
      {
        panelUrl: panelUrl.trim() || null,
        diskLowPercent: Number(diskLow),
        nodeOfflineMinutes: Number(offline),
        smtpHost: host.trim() || null,
        smtpPort: Number(port),
        smtpSecurity: security,
        smtpUserName: user.trim() || null,
        smtpPassword: password || null,
        clearSmtpPassword: clearPassword,
        fromAddress: fromAddress.trim() || null,
        fromName: fromName.trim() || null,
      },
      {
        onSuccess: () => {
          // The password went in; it is never shown again.
          setPassword('');
          setClearPassword(false);
          setSaved(true);
        },
      },
    );
  };

  return (
    <Card>
      <form className="space-y-4" onSubmit={submit} noValidate>
        <Field label={t('notifications.panelUrl')} hint={t('notifications.panelUrlHint')}>
          {(control) => (
            <Input {...control} type="url" value={panelUrl} onChange={(e) => setPanelUrl(e.target.value)} />
          )}
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t('notifications.diskLow')}>
            {(control) => (
              <Input
                {...control}
                type="number"
                min={1}
                max={50}
                value={diskLow}
                onChange={(e) => setDiskLow(e.target.value)}
              />
            )}
          </Field>
          <Field label={t('notifications.nodeOffline')}>
            {(control) => (
              <Input
                {...control}
                type="number"
                min={1}
                max={1440}
                value={offline}
                onChange={(e) => setOffline(e.target.value)}
              />
            )}
          </Field>
        </div>
        <fieldset className="space-y-4">
          <legend className="text-sm font-semibold">{t('notifications.email')}</legend>
          {!settings.smtp && <p className="text-sm text-muted">{t('notifications.emailOff')}</p>}
          <div className="grid gap-4 sm:grid-cols-[1fr_8rem_14rem]">
            <Field label={t('notifications.smtpHost')} hint={t('notifications.smtpHostHint')}>
              {(control) => <Input {...control} value={host} onChange={(e) => setHost(e.target.value)} />}
            </Field>
            <Field label={t('notifications.smtpPort')}>
              {(control) => (
                <Input
                  {...control}
                  type="number"
                  min={1}
                  max={65535}
                  value={port}
                  onChange={(e) => setPort(e.target.value)}
                />
              )}
            </Field>
            <Field label={t('notifications.smtpSecurity')}>
              {(control) => (
                <Select {...control} value={security} onChange={(e) => setSecurity(e.target.value as SmtpSecurity)}>
                  {securities.map((s) => (
                    <option key={s} value={s}>
                      {t(`notifications.securities.${s}`)}
                    </option>
                  ))}
                </Select>
              )}
            </Field>
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t('notifications.smtpUser')}>
              {(control) => (
                <Input {...control} autoComplete="off" value={user} onChange={(e) => setUser(e.target.value)} />
              )}
            </Field>
            <Field
              label={t('notifications.smtpPassword')}
              hint={settings.smtp?.hasPassword ? t('notifications.smtpPasswordKeep') : undefined}
            >
              {(control) => (
                <Input
                  {...control}
                  type="password"
                  autoComplete="new-password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                />
              )}
            </Field>
          </div>
          {settings.smtp?.hasPassword && (
            <Checkbox
              label={t('notifications.clearPassword')}
              checked={clearPassword}
              onChange={(e) => setClearPassword(e.target.checked)}
            />
          )}
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t('notifications.fromAddress')}>
              {(control) => (
                <Input {...control} type="email" value={fromAddress} onChange={(e) => setFromAddress(e.target.value)} />
              )}
            </Field>
            <Field label={t('notifications.fromName')}>
              {(control) => <Input {...control} value={fromName} onChange={(e) => setFromName(e.target.value)} />}
            </Field>
          </div>
        </fieldset>
        {save.isError && <Alert>{errorMessage(t, save.error)}</Alert>}
        <div className="flex items-center gap-3">
          <Button type="submit" disabled={save.isPending}>
            {t('notifications.save')}
          </Button>
          {saved && (
            <p role="status" className="text-sm text-success">
              {t('notifications.saved')}
            </p>
          )}
        </div>
      </form>
    </Card>
  );
}

function ChannelDialog({
  channel,
  emailConfigured,
  onClose,
}: {
  channel: NotificationChannel | null;
  emailConfigured: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const servers = useServers();
  const create = useCreateChannel();
  const update = useUpdateChannel();
  const remove = useDeleteChannel();
  const [name, setName] = useState(channel?.name ?? '');
  const [kind, setKind] = useState<NotificationKind>(channel?.kind ?? 'discord');
  const [webhookUrl, setWebhookUrl] = useState('');
  const [recipients, setRecipients] = useState((channel?.recipients ?? []).join('\n'));
  const [events, setEvents] = useState<Set<NotificationEvent>>(
    new Set(channel?.events ?? ['serverCrashed', 'serverStartFailed', 'backupFailed', 'nodeOffline']),
  );
  const [someServers, setSomeServers] = useState(channel?.serverIds != null);
  const [serverIds, setServerIds] = useState<Set<string>>(new Set(channel?.serverIds ?? []));
  const [language, setLanguage] = useState(channel?.language ?? 'en');
  const [enabled, setEnabled] = useState(channel?.enabled ?? true);
  const saving = create.isPending || update.isPending;
  const error = create.error ?? update.error ?? remove.error;

  const toggle = <T,>(set: Set<T>, value: T, on: boolean) => {
    const next = new Set(set);
    if (on) {
      next.add(value);
    } else {
      next.delete(value);
    }
    return next;
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const body = {
      name: name.trim(),
      kind,
      enabled,
      events: notificationEvents.filter((e) => events.has(e)),
      serverIds: someServers ? [...serverIds] : null,
      language,
      webhookUrl: kind === 'discord' ? webhookUrl.trim() || null : null,
      recipients:
        kind === 'email'
          ? recipients
              .split(/[\n,;]/)
              .map((r) => r.trim())
              .filter(Boolean)
          : null,
    };
    if (channel) {
      update.mutate({ id: channel.id, body }, { onSuccess: onClose });
    } else {
      create.mutate(body, { onSuccess: onClose });
    }
  };

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={channel ? t('notifications.editChannel') : t('notifications.addChannel')}
      size="lg"
    >
      <form className="space-y-4" onSubmit={submit} noValidate>
        <Field label={t('notifications.name')}>
          {(control) => <Input {...control} value={name} maxLength={64} onChange={(e) => setName(e.target.value)} />}
        </Field>
        {!channel && (
          <Field label={t('notifications.kind')}>
            {(control) => (
              <Select {...control} value={kind} onChange={(e) => setKind(e.target.value as NotificationKind)}>
                <option value="discord">{t('notifications.kinds.discord')}</option>
                <option value="email">{t('notifications.kinds.email')}</option>
              </Select>
            )}
          </Field>
        )}
        {kind === 'discord' ? (
          <Field
            label={t('notifications.webhookUrl')}
            hint={
              channel ? t('notifications.webhookKeep', { id: channel.webhook ?? '?' }) : t('notifications.webhookHint')
            }
          >
            {(control) => (
              <Input
                {...control}
                type="url"
                autoComplete="off"
                placeholder="https://discord.com/api/webhooks/…"
                value={webhookUrl}
                onChange={(e) => setWebhookUrl(e.target.value)}
              />
            )}
          </Field>
        ) : (
          <>
            <Field label={t('notifications.recipients')} hint={t('notifications.recipientsHint')}>
              {(control) => (
                <textarea
                  {...control}
                  rows={3}
                  value={recipients}
                  onChange={(e) => setRecipients(e.target.value)}
                  className="w-full rounded-md border border-border bg-bg px-3 py-2 text-sm"
                />
              )}
            </Field>
            {!emailConfigured && <Alert>{t('notifications.emailNeedsSmtp')}</Alert>}
          </>
        )}
        <fieldset className="space-y-2">
          <legend className="text-sm font-medium">{t('notifications.events')}</legend>
          {notificationEvents.map((e) => (
            <Checkbox
              key={e}
              label={t(`notifications.eventNames.${e}`)}
              checked={events.has(e)}
              onChange={(change) => setEvents(toggle(events, e, change.target.checked))}
            />
          ))}
        </fieldset>
        <fieldset className="space-y-2">
          <legend className="text-sm font-medium">{t('notifications.servers')}</legend>
          <Checkbox
            label={t('notifications.onlySomeServers')}
            checked={someServers}
            onChange={(e) => setSomeServers(e.target.checked)}
          />
          {someServers && (
            <div className="ml-6 max-h-40 space-y-1 overflow-y-auto">
              {(servers.data ?? []).map((server) => (
                <Checkbox
                  key={server.id}
                  label={`${server.name} (${server.nodeName})`}
                  checked={serverIds.has(server.id)}
                  onChange={(e) => setServerIds(toggle(serverIds, server.id, e.target.checked))}
                />
              ))}
            </div>
          )}
          <p className="text-xs text-muted">{t('notifications.serversHint')}</p>
        </fieldset>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t('notifications.language')}>
            {(control) => (
              <Select {...control} value={language} onChange={(e) => setLanguage(e.target.value)}>
                <option value="en">{t('notifications.languages.en')}</option>
                <option value="es">{t('notifications.languages.es')}</option>
              </Select>
            )}
          </Field>
          <div className="flex items-end pb-2">
            <Checkbox
              label={t('notifications.enabled')}
              checked={enabled}
              onChange={(e) => setEnabled(e.target.checked)}
            />
          </div>
        </div>
        {error && <Alert>{errorMessage(t, error)}</Alert>}
        <div className="flex items-center justify-between gap-2">
          {channel ? (
            <Button
              type="button"
              variant="danger"
              disabled={remove.isPending}
              onClick={() =>
                window.confirm(t('notifications.deleteConfirm', { name: channel.name })) &&
                remove.mutate(channel.id, { onSuccess: onClose })
              }
            >
              {t('notifications.deleteChannel')}
            </Button>
          ) : (
            <span />
          )}
          <div className="flex gap-2">
            <Button type="button" variant="ghost" onClick={onClose}>
              {t('actions.cancel')}
            </Button>
            <Button type="submit" disabled={saving}>
              {t('actions.save')}
            </Button>
          </div>
        </div>
      </form>
    </Dialog>
  );
}
