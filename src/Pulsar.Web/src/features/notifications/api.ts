import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { invalidateAfterChange } from '../../lib/queries';
import { adminKeys } from '../admin/api';

export type NotificationChannel = Schemas['NotificationChannelView'];
export type NotificationChannelRequest = Schemas['NotificationChannelRequest'];
export type NotificationEvent = Schemas['NotificationEvent'];
export type NotificationKind = Schemas['NotificationKind'];
export type NotificationSettings = Schemas['NotificationSettingsView'];
export type NotificationSettingsRequest = Schemas['NotificationSettingsRequest'];
export type NotificationDelivery = Schemas['NotificationDeliveryView'];
export type SmtpSecurity = Schemas['SmtpSecurity'];

export const notificationEvents: NotificationEvent[] = [
  'serverCrashed',
  'serverStartFailed',
  'backupFailed',
  'scheduledTaskFailed',
  'nodeOffline',
  'nodeDiskLow',
  'publicIpCertificate',
];

export const notificationKeys = {
  channels: ['notifications', 'channels'] as const,
  settings: ['notifications', 'settings'] as const,
  deliveries: ['notifications', 'deliveries'] as const,
};

export function useNotificationChannels() {
  return useQuery({
    queryKey: notificationKeys.channels,
    queryFn: () => unwrap(api.GET('/api/v1/notifications/channels')),
  });
}

export function useNotificationSettings() {
  return useQuery({
    queryKey: notificationKeys.settings,
    queryFn: () => unwrap(api.GET('/api/v1/notifications/settings')),
  });
}

/** Recent messages; refreshed while some are still waiting to be sent or retried. */
export function useNotificationDeliveries() {
  return useQuery({
    queryKey: notificationKeys.deliveries,
    queryFn: () => unwrap(api.GET('/api/v1/notifications/deliveries')),
    refetchInterval: (query) => (query.state.data?.deliveries.some((d) => d.status === 'pending') ? 5000 : false),
  });
}

function useChanged() {
  const queryClient = useQueryClient();
  return () =>
    invalidateAfterChange(
      queryClient,
      { queryKey: notificationKeys.channels },
      { queryKey: notificationKeys.deliveries },
      { queryKey: adminKeys.audit },
    );
}

export function useCreateChannel() {
  const changed = useChanged();
  return useMutation({
    mutationFn: (body: NotificationChannelRequest) => unwrap(api.POST('/api/v1/notifications/channels', { body })),
    onSuccess: changed,
  });
}

export function useUpdateChannel() {
  const changed = useChanged();
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: NotificationChannelRequest }) =>
      unwrap(api.PUT('/api/v1/notifications/channels/{id}', { params: { path: { id } }, body })),
    onSuccess: changed,
  });
}

export function useDeleteChannel() {
  const changed = useChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE('/api/v1/notifications/channels/{id}', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

export function useTestChannel() {
  const changed = useChanged();
  return useMutation({
    mutationFn: (id: string) =>
      unwrap(api.POST('/api/v1/notifications/channels/{id}/test', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

export function useUpdateNotificationSettings() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: NotificationSettingsRequest) => unwrap(api.PUT('/api/v1/notifications/settings', { body })),
    onSuccess: (saved) => {
      queryClient.setQueryData(notificationKeys.settings, saved);
      return queryClient.invalidateQueries({ queryKey: adminKeys.audit });
    },
  });
}
