import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { initI18n } from '../../app/i18n';
import { fakeApi, json, problem, publicIpCertificateOff } from '../../test/fakeApi';
import { checkPublicIp } from './api';
import { PublicIpCertificateSection } from './PublicIpCertificateSection';

const serving = {
  ...publicIpCertificateOff,
  enabled: true,
  publicIp: '198.51.99.10',
  listening: true,
  serving: true,
  certificate: {
    ip: '198.51.99.10',
    notBefore: '2026-10-17T10:00:00Z',
    notAfter: '2026-10-24T02:00:00Z',
    sha256: 'a'.repeat(64),
    staging: false,
  },
  lastRenewalAt: '2026-10-17T10:01:00Z',
  lastAttemptAt: '2026-10-17T10:00:30Z',
  nextAttemptAt: '2026-10-20T18:00:00Z',
  attemptsLeft: 2,
};

function renderWithClient(ui: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('checkPublicIp', () => {
  it.each(['1.2.3.4', '8.8.8.8', '100.128.0.1', '172.32.0.1', '2001:4860:4860::8888', '2a00:1450:4001::200e'])(
    'takes the public address %s',
    (address) => expect(checkPublicIp(address)).toBe('ok'),
  );

  it.each([
    '10.0.0.1',
    '172.16.0.1',
    '192.168.1.10',
    '127.0.0.1',
    '169.254.1.1',
    '100.64.0.1',
    '192.0.2.10',
    '203.0.113.10',
    '224.0.0.1',
    '255.255.255.255',
    '::1',
    'fe80::1',
    'fd00::1',
    '2001:db8::1',
    '2002::1',
  ])('refuses the local, shared or reserved address %s', (address) => expect(checkPublicIp(address)).toBe('notPublic'));

  it.each([
    'panel.example.com',
    '1.2.3',
    '01.2.3.4',
    '1.2.3.256',
    '1.2.3.4:8443',
    '[2001:db8::1]',
    '::ffff:8.8.8.8',
    'zz::1',
  ])('refuses %s, which is not an address literal', (text) => expect(checkPublicIp(text)).toBe('invalid'));
});

describe('Public IP certificate', () => {
  it('turns it on after saying what is agreed to, with the router reminder and the terms link', async () => {
    const put = vi.fn(async (request: Request) => {
      expect(await request.json()).toEqual({
        enabled: true,
        publicIp: '198.51.99.10',
        email: null,
        directory: 'production',
      });
      return json({ ...serving, serving: false, certificate: null, renewing: true });
    });
    const confirm = vi.fn(() => true);
    vi.stubGlobal('confirm', confirm);
    fakeApi({
      'GET /api/v1/public-ip-certificate': () => json(publicIpCertificateOff),
      'PUT /api/v1/public-ip-certificate': put,
      'GET /api/v1/audit': () => json({ items: [], nextCursor: null }),
    });
    renderWithClient(<PublicIpCertificateSection />);
    const user = userEvent.setup();

    expect(await screen.findByText(/forward TCP ports 80 and/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Subscriber Agreement' })).toHaveAttribute(
      'href',
      'https://letsencrypt.org/repository/',
    );
    await user.click(screen.getByLabelText("Use a Let's Encrypt certificate for my public IP"));
    await user.type(screen.getByLabelText('Public IP address'), '198.51.99.10');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(confirm).toHaveBeenCalledWith(expect.stringContaining("You accept Let's Encrypt's Subscriber Agreement"));
    expect(put).toHaveBeenCalledOnce();
    expect(await screen.findByText("Asking Let's Encrypt…")).toBeInTheDocument();
    expect(screen.getByText('No valid certificate yet')).toBeInTheDocument();
  });

  it('refuses a private address before asking the panel', async () => {
    const put = vi.fn(async () => json(serving));
    vi.stubGlobal(
      'confirm',
      vi.fn(() => true),
    );
    fakeApi({
      'GET /api/v1/public-ip-certificate': () => json(publicIpCertificateOff),
      'PUT /api/v1/public-ip-certificate': put,
    });
    renderWithClient(<PublicIpCertificateSection />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('checkbox'));
    await user.type(screen.getByLabelText('Public IP address'), '192.168.1.10');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText(/That is not a public internet address/)).toBeInTheDocument();
    expect(put).not.toHaveBeenCalled();
  });

  it('shows the certificate in use, its dates and the port, and renews it on demand', async () => {
    const renew = vi.fn(async () => json({ ...serving, renewing: true, attemptsLeft: 1 }, 202));
    fakeApi({
      'GET /api/v1/public-ip-certificate': () => json(serving),
      'POST /api/v1/public-ip-certificate/renew': renew,
      'GET /api/v1/audit': () => json({ items: [], nextCursor: null }),
    });
    renderWithClient(<PublicIpCertificateSection />);
    const user = userEvent.setup();

    expect(await screen.findByText('Certificate in use')).toBeInTheDocument();
    expect(screen.getByText('Port 80 open for the check')).toBeInTheDocument();
    expect(screen.getByText('Valid until')).toBeInTheDocument();
    expect(screen.getByText('2 more attempts allowed this hour.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Renew now' }));

    expect(renew).toHaveBeenCalledOnce();
    expect(await screen.findByText('1 more attempt allowed this hour.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Renew now' })).toBeDisabled();
  });

  it('says why the last attempt failed and why port 80 is not open', async () => {
    fakeApi({
      'GET /api/v1/public-ip-certificate': () =>
        json({
          ...serving,
          serving: false,
          certificate: null,
          listening: false,
          listenError: 'Port 80 could not be opened: address already in use',
          lastError: "Let's Encrypt could not check port 80 of 198.51.99.10: Timeout during connect",
          lastErrorAt: '2026-10-17T10:00:40Z',
          attemptsLeft: 0,
        }),
    });
    renderWithClient(<PublicIpCertificateSection />);

    expect(await screen.findByText(/Timeout during connect/)).toBeInTheDocument();
    expect(screen.getByText('Port 80 could not be opened: address already in use')).toBeInTheDocument();
    expect(screen.getByText('Port 80 not open')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Renew now' })).toBeDisabled();
  });

  it('shows the panel refusing the renewal', async () => {
    fakeApi({
      'GET /api/v1/public-ip-certificate': () => json(serving),
      'POST /api/v1/public-ip-certificate/renew': () => problem(409, 'public_ip_certificate.too_many_attempts'),
    });
    renderWithClient(<PublicIpCertificateSection />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Renew now' }));

    expect(await screen.findByText(/already asked 3 times this hour/)).toBeInTheDocument();
  });
});
