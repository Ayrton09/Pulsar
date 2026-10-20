import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { adminKeys } from '../admin/api';

export type SftpConnection = Schemas['SftpConnectionResponse'];
export type SftpSettings = Schemas['SftpSettingsResponse'];
export type SshKey = Schemas['SshKeyView'];

export const sftpKeys = {
  connection: ['sftp', 'connection'] as const,
  settings: ['sftp', 'settings'] as const,
  sshKeys: ['account', 'sshKeys'] as const,
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

/** The signed-in user's SSH keys for SFTP, newest first. */
export function useSshKeys() {
  return useQuery({ queryKey: sftpKeys.sshKeys, queryFn: () => unwrap(api.GET('/api/v1/account/ssh-keys')) });
}

function useSshKeysChanged() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: sftpKeys.sshKeys }),
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
}

/** Adds a key: the password, and the code from the app when two-factor authentication is on. */
export function useAddSshKey() {
  const changed = useSshKeysChanged();
  return useMutation({
    mutationFn: (body: Schemas['AddSshKeyRequest']) => unwrap(api.POST('/api/v1/account/ssh-keys', { body })),
    onSuccess: changed,
  });
}

/** Deletes a key: the SFTP sessions it signed in end. */
export function useDeleteSshKey() {
  const changed = useSshKeysChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE('/api/v1/account/ssh-keys/{id}', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

/** A key's type as people name it: "Ed25519", "ECDSA P-384", "RSA 4096". */
export function sshKeyType(type: string, bits: number): string {
  if (type === 'ssh-ed25519') {
    return 'Ed25519';
  }
  if (type === 'ssh-rsa') {
    return `RSA ${bits}`;
  }
  return type.startsWith('ecdsa-sha2-nistp') ? `ECDSA P-${bits}` : type;
}

/** The comment after a pasted key's type and key, which names it when no name is given; empty when there is none. */
export function sshKeyComment(text: string): string {
  return /^\S+[ \t]+\S+[ \t]+(.+)$/.exec(text.trim())?.[1]?.trim() ?? '';
}

/** `sftp://user@host:port/`, with an IPv6 address in brackets. */
export function sftpUrl(userName: string, host: string, port: number): string {
  const bracketed = host.includes(':') && !host.startsWith('[') ? `[${host}]` : host;
  return `sftp://${encodeURIComponent(userName)}@${bracketed}:${port}/`;
}
