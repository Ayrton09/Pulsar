import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { adminKeys } from '../admin/api';

export type SftpConnection = Schemas['SftpConnectionResponse'];
export type SftpSettings = Schemas['SftpSettingsResponse'];

export const sftpKeys = {
  connection: ['sftp', 'connection'] as const,
  settings: ['sftp', 'settings'] as const,
};

/** How the signed-in user connects: the host, the port and the host key to check at the first connection. */
export function useSftpConnection() {
  return useQuery({
    queryKey: sftpKeys.connection,
    queryFn: () => unwrap(api.GET('/api/v1/sftp')),
  });
}

export function useSftpSettings() {
  return useQuery({
    queryKey: sftpKeys.settings,
    queryFn: () => unwrap(api.GET('/api/v1/sftp/settings')),
  });
}

export function useUpdateSftpSettings() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: Schemas['UpdateSftpSettingsRequest']) => unwrap(api.PUT('/api/v1/sftp/settings', { body })),
    onSuccess: async (settings) => {
      queryClient.setQueryData(sftpKeys.settings, settings);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: sftpKeys.connection }),
        queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
      ]);
    },
  });
}

/** `sftp://user@host:port/`, with an IPv6 address in brackets. */
export function sftpUrl(userName: string, host: string, port: number): string {
  const bracketed = host.includes(':') && !host.startsWith('[') ? `[${host}]` : host;
  return `sftp://${encodeURIComponent(userName)}@${bracketed}:${port}/`;
}
