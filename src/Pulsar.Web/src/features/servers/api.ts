import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { adminKeys } from '../admin/api';

export type ServerSummary = Schemas['ServerSummary'];
export type ServerDetails = Schemas['ServerDetails'];
export type OperationView = Schemas['OperationView'];
export type TemplateView = Schemas['TemplateView'];
export type TemplateDetails = Schemas['TemplateDetails'];
export type TemplateVariable = Schemas['TemplateVariable'];
export type ServerSettings = Schemas['ServerSettings'];

export const serverKeys = {
  all: ['servers'] as const,
  detail: (id: string) => ['servers', id] as const,
  settings: (id: string) => ['servers', id, 'settings'] as const,
  templates: ['templates'] as const,
  template: (id: string) => ['templates', id] as const,
};

/** Kept fresh by the live channel (useWatchServers), not by polling. */
export function useServers(enabled = true) {
  return useQuery({
    queryKey: serverKeys.all,
    queryFn: () => unwrap(api.GET('/api/v1/servers')),
    enabled,
  });
}

/** Kept fresh by the live channel (useWatchServers), not by polling. */
export function useServer(id: string) {
  return useQuery({
    queryKey: serverKeys.detail(id),
    queryFn: () => unwrap(api.GET('/api/v1/servers/{id}', { params: { path: { id } } })),
  });
}

/** Sends a line to the server's console (its output shows up in the console stream). */
export function useConsoleCommand() {
  return useMutation({
    mutationFn: ({ id, command }: { id: string; command: string }) =>
      unwrap(api.POST('/api/v1/servers/{id}/console', { params: { path: { id } }, body: { command } })),
  });
}

export function useTemplates(enabled = true) {
  return useQuery({
    queryKey: serverKeys.templates,
    queryFn: () => unwrap(api.GET('/api/v1/templates')),
    enabled,
  });
}

/** Variables, ports and requirements of a game: what the creation form asks for. */
export function useTemplate(id: string | undefined) {
  return useQuery({
    queryKey: serverKeys.template(id ?? ''),
    queryFn: () => unwrap(api.GET('/api/v1/templates/{id}', { params: { path: { id: id ?? '' } } })),
    enabled: !!id,
  });
}

export function useServerSettings(id: string) {
  return useQuery({
    queryKey: serverKeys.settings(id),
    queryFn: () => unwrap(api.GET('/api/v1/servers/{id}/settings', { params: { path: { id } } })),
  });
}

/** Server changes show up in the list, the server page and the audit log. */
function useServersChanged() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: serverKeys.all }),
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
}

export function useCreateServer() {
  const changed = useServersChanged();
  return useMutation({
    mutationFn: (body: Schemas['CreateServerRequest']) => unwrap(api.POST('/api/v1/servers', { body })),
    onSuccess: changed,
  });
}

export type ServerCommand = 'start' | 'stop' | 'restart' | 'kill';

export function useServerCommand() {
  const changed = useServersChanged();
  return useMutation({
    mutationFn: ({ id, command }: { id: string; command: ServerCommand }) => {
      const options = { params: { path: { id } } };
      switch (command) {
        case 'start':
          return unwrap(api.POST('/api/v1/servers/{id}/start', options));
        case 'stop':
          return unwrap(api.POST('/api/v1/servers/{id}/stop', options));
        case 'restart':
          return unwrap(api.POST('/api/v1/servers/{id}/restart', options));
        case 'kill':
          return unwrap(api.POST('/api/v1/servers/{id}/kill', options));
      }
    },
    onSuccess: changed,
  });
}

export function useReinstallServer() {
  const changed = useServersChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST('/api/v1/servers/{id}/install', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

export function useUpdateServerSettings() {
  const changed = useServersChanged();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: Schemas['UpdateServerSettingsRequest'] }) =>
      unwrap(api.PUT('/api/v1/servers/{id}/settings', { params: { path: { id } }, body })),
    onSuccess: (settings, { id }) => {
      queryClient.setQueryData(serverKeys.settings(id), settings);
      return changed();
    },
  });
}

export function useUpdateServerPorts() {
  const changed = useServersChanged();
  return useMutation({
    mutationFn: ({ id, ports }: { id: string; ports: Record<string, string | null> }) =>
      unwrap(api.PUT('/api/v1/servers/{id}/ports', { params: { path: { id } }, body: { ports } })),
    onSuccess: changed,
  });
}

export function useUpdateGame() {
  const changed = useServersChanged();
  return useMutation({
    mutationFn: ({ id, validate }: { id: string; validate: boolean }) =>
      unwrap(api.POST('/api/v1/servers/{id}/update', { params: { path: { id } }, body: { validate } })),
    onSuccess: changed,
  });
}

export function useCancelOperation() {
  const changed = useServersChanged();
  return useMutation({
    mutationFn: ({ id, operationId }: { id: string; operationId: string }) =>
      unwrap(
        api.POST('/api/v1/servers/{id}/operations/{operationId}/cancel', { params: { path: { id, operationId } } }),
      ),
    onSuccess: changed,
  });
}

export function useTemplatePreview() {
  return useMutation({
    mutationFn: (content: string) => unwrap(api.POST('/api/v1/templates/preview', { body: { content } })),
  });
}

export function useImportTemplate() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (content: string) => unwrap(api.POST('/api/v1/templates', { body: { content } })),
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: serverKeys.templates }),
        queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
      ]),
  });
}

export function useDeleteServer() {
  const changed = useServersChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE('/api/v1/servers/{id}', { params: { path: { id } } })),
    onSuccess: changed,
  });
}
