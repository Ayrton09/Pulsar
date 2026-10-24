import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import i18next from 'i18next';
import type { ReactNode } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { initI18n, loadLanguage } from '../../app/i18n';
import { fakeApi, json, problem } from '../../test/fakeApi';
import { formatFingerprint, type PanelCertificate } from './api';
import { PanelCertificateSection } from './PanelCertificateSection';

const sha256 = '3f9a0c1e' + '5d'.repeat(28);
const installers: PanelCertificate = {
  sha256,
  dnsNames: ['localhost', 'GAMES-PC'],
  ipAddresses: ['127.0.0.1', '::1', '192.168.1.20'],
  notAfter: '2031-10-17T12:00:00+00:00',
  selfSigned: true,
  trustable: true,
};

function renderWithClient(ui: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(async () => {
  vi.unstubAllGlobals();
  await i18next.changeLanguage('en');
});

describe('formatFingerprint', () => {
  it('writes a SHA-256 as browsers show fingerprints', () => {
    expect(formatFingerprint('0123456789abcdef'.repeat(4))).toBe(
      '01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF',
    );
    expect(formatFingerprint('')).toBe('');
  });
});

describe('the panel certificate on the account page', () => {
  it("shows the installer's certificate with its fingerprint, the download and the steps to trust it", async () => {
    const api = fakeApi({ 'GET /api/v1/panel/certificate': () => json(installers) });
    renderWithClient(<PanelCertificateSection />);
    const user = userEvent.setup();
    const writeText = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();

    const fingerprint = `3F:9A:0C:1E:${Array(28).fill('5D').join(':')}`;
    expect(await screen.findByText(fingerprint)).toBeInTheDocument();
    expect(
      screen.getByText(/For localhost, GAMES-PC, 127\.0\.0\.1, ::1, 192\.168\.1\.20\. Valid until/),
    ).toBeInTheDocument();
    const download = screen.getByRole('link', { name: 'Download certificate' });
    expect(download).toHaveAttribute('href', '/api/v1/panel/certificate/download');
    expect(download).toHaveAttribute('download');
    // Over a connection the browser does not trust yet, the page itself could be someone else's.
    expect(screen.getByRole('note')).toHaveTextContent(/Get-FileHash pulsar-panel\.cer/);
    expect(screen.getByRole('note')).toHaveTextContent(/do not go by the fingerprint above alone/);
    expect(screen.getByText(/Trusted Root Certification Authorities/)).toBeInTheDocument();
    expect(screen.getByText(/Choose Current User/)).toBeInTheDocument();
    expect(screen.getByText(/Firefox keeps its own list/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Copy Fingerprint (SHA-256)' }));
    expect(writeText).toHaveBeenCalledWith(fingerprint);
    // The file is a plain link: nothing but the description is fetched.
    expect(api.mock.calls.map(([request]) => new URL((request as Request).url).pathname)).toEqual([
      '/api/v1/panel/certificate',
    ]);
  });

  it('says in Spanish, with voseo, why and how', async () => {
    await loadLanguage('es');
    await i18next.changeLanguage('es');
    fakeApi({ 'GET /api/v1/panel/certificate': () => json(installers) });
    renderWithClient(<PanelCertificateSection />);

    expect(await screen.findByRole('link', { name: 'Descargar certificado' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Certificado del panel' })).toBeInTheDocument();
    expect(screen.getByText(/Para que tu navegador no avise/)).toBeInTheDocument();
    expect(screen.getByText(/Entidades de certificación raíz de confianza/)).toBeInTheDocument();
    expect(screen.getByRole('note')).toHaveTextContent(/Comprobá que sea el de tu panel/);
  });

  it('offers nothing to install for a certificate an authority issued', async () => {
    fakeApi({ 'GET /api/v1/panel/certificate': () => json({ ...installers, selfSigned: false, trustable: false }) });
    renderWithClient(<PanelCertificateSection />);

    expect(await screen.findByText(/An authority issued this certificate/)).toBeInTheDocument();
    expect(screen.getByText(formatFingerprint(sha256))).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Download certificate' })).not.toBeInTheDocument();
  });

  it('says not to trust a self-signed certificate that could issue others', async () => {
    fakeApi({ 'GET /api/v1/panel/certificate': () => json({ ...installers, trustable: false }) });
    renderWithClient(<PanelCertificateSection />);

    expect(await screen.findByText(/do not install it as trusted/)).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Download certificate' })).not.toBeInTheDocument();
    expect(screen.queryByText(/Trusted Root Certification Authorities/)).not.toBeInTheDocument();
  });

  it('shows a short note when the panel cannot tell which certificate this browser saw', async () => {
    fakeApi({ 'GET /api/v1/panel/certificate': () => problem(404, 'panel.certificate_unavailable') });
    renderWithClient(<PanelCertificateSection />);

    expect(await screen.findByText(/did not reach the panel over its own HTTPS/)).toBeInTheDocument();
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it("says there is nothing to install over the public IP certificate (Let's Encrypt)", async () => {
    fakeApi({ 'GET /api/v1/panel/certificate': () => problem(404, 'panel.certificate_publicly_trusted') });
    renderWithClient(<PanelCertificateSection />);

    expect(await screen.findByText(/which every browser trusts already/)).toBeInTheDocument();
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows any other failure as an error', async () => {
    fakeApi({ 'GET /api/v1/panel/certificate': () => problem(500, 'http.500', 'boom') });
    renderWithClient(<PanelCertificateSection />);

    expect(await screen.findByRole('alert')).toHaveTextContent('Something went wrong. Try again.');
  });
});
