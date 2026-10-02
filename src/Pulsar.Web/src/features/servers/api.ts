import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { adminKeys } from '../admin/api';

export type ServerSummary = Schemas['ServerSummary'];
export type ServerDetails = Schemas['ServerDetails'];
export type OperationView = Schemas['OperationView'];
export type TemplateView = Schemas['TemplateView'];

/**
 * Server states change in seconds (starting, stopping, an install finishing): poll faster than nodes. Phase 5 replaces
 * polling with the browser's live channel.
 */
const REFRESH_MS = 2_000;

export const serverKeys = {
  all: ['servers'] as const,
  detail: (id: string) => ['servers', id] as const,
  templates: ['templates'] as const,
};

export function useServers() {
  return useQuery({
    queryKey: serverKeys.all,
    queryFn: () => unwrap(api.GET('/api/v1/servers')),
    refetchInterval: REFRESH_MS,
  });
}

export function useServer(id: string) {
  return useQuery({
    queryKey: serverKeys.detail(id),
    queryFn: () => unwrap(api.GET('/api/v1/servers/{id}', { params: { path: { id } } })),
    refetchInterval: REFRESH_MS,
  });
}

export function useTemplates(enabled = true) {
  return useQuery({
    queryKey: serverKeys.templates,
    queryFn: () => unwrap(api.GET('/api/v1/templates')),
    enabled,
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

export function useDeleteServer() {
  const changed = useServersChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE('/api/v1/servers/{id}', { params: { path: { id } } })),
    onSuccess: changed,
  });
}
