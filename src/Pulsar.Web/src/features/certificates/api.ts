import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { adminKeys } from '../admin/api';

export type PublicIpCertificate = Schemas['PublicIpCertificateResponse'];
export type AcmeDirectoryKind = Schemas['AcmeDirectoryKind'];

export const publicIpCertificateKeys = {
  status: ['public-ip-certificate'] as const,
};

/** The settings and status; looked at again every few seconds while a certificate is being asked for or is missing. */
export function usePublicIpCertificate() {
  return useQuery({
    queryKey: publicIpCertificateKeys.status,
    queryFn: () => unwrap(api.GET('/api/v1/public-ip-certificate')),
    refetchInterval: (query) => {
      const data = query.state.data;
      return data?.enabled && (data.renewing || !data.serving) ? 5000 : false;
    },
  });
}

export function useUpdatePublicIpCertificate() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: Schemas['UpdatePublicIpCertificateRequest']) =>
      unwrap(api.PUT('/api/v1/public-ip-certificate', { body })),
    onSuccess: async (status) => {
      queryClient.setQueryData(publicIpCertificateKeys.status, status);
      await queryClient.invalidateQueries({ queryKey: adminKeys.audit });
    },
  });
}

export function useRenewPublicIpCertificate() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/public-ip-certificate/renew')),
    onSuccess: async (status) => {
      queryClient.setQueryData(publicIpCertificateKeys.status, status);
      await queryClient.invalidateQueries({ queryKey: adminKeys.audit });
    },
  });
}

const privateV4: [number, number][] = [
  [0x00000000, 8],
  [0x0a000000, 8],
  [0x64400000, 10],
  [0x7f000000, 8],
  [0xa9fe0000, 16],
  [0xac100000, 12],
  [0xc0000000, 24],
  [0xc0000200, 24],
  [0xc0586300, 24],
  [0xc0a80000, 16],
  [0xc6120000, 15],
  [0xc6336400, 24],
  [0xcb007100, 24],
  [0xe0000000, 4],
  [0xf0000000, 4],
];

/**
 * What the panel will say of an address before it is sent (it checks again): an IPv4 or IPv6 literal that the internet
 * reaches, or why not. Private, local, shared (carrier-grade NAT), documentation, multicast and reserved ranges are not.
 */
export function checkPublicIp(text: string): 'ok' | 'invalid' | 'notPublic' {
  const value = text.trim();
  if (/^\d{1,3}(\.\d{1,3}){3}$/.test(value)) {
    const parts = value.split('.');
    if (parts.some((p) => (p.length > 1 && p.startsWith('0')) || Number(p) > 255)) {
      return 'invalid';
    }
    const bits = parts.reduce((acc, p) => acc * 256 + Number(p), 0) >>> 0;
    const local = privateV4.some(
      ([network, prefix]) => (bits & ((0xffffffff << (32 - prefix)) >>> 0)) >>> 0 === network,
    );
    return local ? 'notPublic' : 'ok';
  }
  if (!/^[0-9a-fA-F:.]+$/.test(value) || !value.includes(':')) {
    return 'invalid';
  }
  let hostname: string;
  try {
    hostname = new URL(`http://[${value}]/`).hostname.slice(1, -1);
  } catch {
    return 'invalid';
  }
  if (hostname.startsWith('::ffff:')) {
    return 'invalid';
  }
  const first = parseInt(hostname.split(':')[0] || '0', 16);
  const second = parseInt(hostname.split(':')[1] || '0', 16);
  const global =
    (first & 0xe000) === 0x2000 &&
    !(first === 0x2001 && second < 0x0200) &&
    !(first === 0x2001 && second === 0x0db8) &&
    first !== 0x2002 &&
    !(first === 0x3fff && (second & 0xf000) === 0);
  return global ? 'ok' : 'notPublic';
}
