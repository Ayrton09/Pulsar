import { useQuery } from '@tanstack/react-query';
import { ApiError, api, unwrap, type Schemas } from '../../api/client';

export type PanelCertificate = Schemas['PanelCertificateResponse'];

export const panelCertificateKeys = {
  current: ['panel', 'certificate'] as const,
};

/** Where the browser downloads the panel's certificate from (a plain link: the panel answers with its DER file). */
export const panelCertificateDownloadPath = '/api/v1/panel/certificate/download';

/** What the panel answers when it cannot tell which certificate this browser was served (plain HTTP, a proxy). */
export const CERTIFICATE_UNAVAILABLE = 'panel.certificate_unavailable';

/** What it answers when this browser got the public IP certificate (Let's Encrypt), which every browser trusts. */
export const CERTIFICATE_PUBLICLY_TRUSTED = 'panel.certificate_publicly_trusted';

/**
 * The certificate the panel presented on this browser's connection (docs/guides/install.md, "Connecting from another
 * PC"), null when the panel cannot tell which one the browser saw, or 'publiclyTrusted' when it was the public IP
 * certificate (Let's Encrypt): nothing to download or trust.
 */
export function usePanelCertificate() {
  return useQuery({
    queryKey: panelCertificateKeys.current,
    queryFn: async (): Promise<PanelCertificate | null | 'publiclyTrusted'> => {
      try {
        return await unwrap(api.GET('/api/v1/panel/certificate'));
      } catch (error) {
        if (error instanceof ApiError && error.code === CERTIFICATE_UNAVAILABLE) {
          return null;
        }
        if (error instanceof ApiError && error.code === CERTIFICATE_PUBLICLY_TRUSTED) {
          return 'publiclyTrusted';
        }
        throw error;
      }
    },
  });
}

/** A SHA-256 (lowercase hex) as browsers show a certificate's fingerprint: upper case, a colon between each two digits. */
export function formatFingerprint(sha256: string): string {
  return (sha256.toUpperCase().match(/.{1,2}/g) ?? []).join(':');
}
