import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { initI18n } from '../../app/i18n';
import { fakeApi, json, problem } from '../../test/fakeApi';
import { sftpUrl } from './api';
import { SftpAccessSection } from './SftpAccessSection';
import { SftpServerHint } from './SftpServerHint';
import { SftpSettingsSection } from './SftpSettingsSection';

const fingerprint = 'SHA256:q8URNWahibMuP6HE3x1bfk3+IBKJXOI1AsIo1/SmroU';
const connection = {
  enabled: true,
  host: 'panel.example.com',
  port: 2022,
  hostKeyFingerprint: fingerprint,
  userName: 'alice',
};
const settings = { enabled: true, port: 2022, publicHost: null, hostKeyFingerprint: fingerprint, listening: true };

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

describe('sftpUrl', () => {
  it('builds the address, bracketing IPv6 hosts', () => {
    expect(sftpUrl('alice', 'panel.example.com', 2022)).toBe('sftp://alice@panel.example.com:2022/');
    expect(sftpUrl('alice.abcd2345', '2001:db8::1', 2200)).toBe('sftp://alice.abcd2345@[2001:db8::1]:2200/');
  });
});

describe('SFTP access on the account page', () => {
  it('shows the address and the host key fingerprint, with the two-factor note', async () => {
    fakeApi({ 'GET /api/v1/sftp': () => json(connection) });
    renderWithClient(<SftpAccessSection />);
    const user = userEvent.setup();
    const writeText = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();

    expect(await screen.findByText('sftp://alice@panel.example.com:2022/')).toBeInTheDocument();
    expect(screen.getByText(fingerprint)).toBeInTheDocument();
    expect(screen.getByText(/Recovery codes do not work over SFTP/)).toBeInTheDocument();
    expect(screen.getByText(/connect only if it is exactly this one/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Copy Address' }));
    expect(writeText).toHaveBeenCalledWith('sftp://alice@panel.example.com:2022/');
  });

  it('says when SFTP is turned off', async () => {
    fakeApi({ 'GET /api/v1/sftp': () => json({ ...connection, enabled: false }) });
    renderWithClient(<SftpAccessSection />);

    expect(await screen.findByText(/SFTP is turned off on this panel/)).toBeInTheDocument();
    expect(screen.queryByText(fingerprint)).not.toBeInTheDocument();
  });
});

describe("SFTP hint in a server's files", () => {
  it("gives the address whose root is the server's folder", async () => {
    fakeApi({ 'GET /api/v1/sftp': () => json(connection) });
    renderWithClient(<SftpServerHint shortId="abcd2345" />);

    expect(await screen.findByText('sftp://alice.abcd2345@panel.example.com:2022/')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: "Copy the server's SFTP address" })).toBeInTheDocument();
  });

  it('shows nothing while SFTP is off', async () => {
    const mock = fakeApi({ 'GET /api/v1/sftp': () => json({ ...connection, enabled: false }) });
    renderWithClient(<SftpServerHint shortId="abcd2345" />);

    await vi.waitFor(() => expect(mock).toHaveBeenCalled());
    expect(screen.queryByText(/Connect with SFTP/)).not.toBeInTheDocument();
  });
});

describe('SFTP settings', () => {
  it('saves a new public host without asking, since it ends no connection', async () => {
    const put = vi.fn(async (request: Request) => {
      expect(await request.json()).toEqual({ enabled: true, port: 2022, publicHost: 'files.example.com' });
      return json({ ...settings, publicHost: 'files.example.com' });
    });
    const confirm = vi.fn(() => true);
    vi.stubGlobal('confirm', confirm);
    fakeApi({
      'GET /api/v1/sftp/settings': () => json(settings),
      'GET /api/v1/sftp': () => json(connection),
      'GET /api/v1/audit': () => json({ items: [], nextCursor: null }),
      'PUT /api/v1/sftp/settings': put,
    });
    renderWithClient(<SftpSettingsSection />);
    const user = userEvent.setup();

    expect(await screen.findByText(fingerprint)).toBeInTheDocument();
    expect(screen.getByText('Listening')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Public host'), 'files.example.com');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Changes saved.')).toBeInTheDocument();
    expect(put).toHaveBeenCalledOnce();
    expect(confirm).not.toHaveBeenCalled();
  });

  it('asks before changing the port, and does nothing if refused', async () => {
    const put = vi.fn(async () => json(settings));
    const confirm = vi.fn(() => false);
    vi.stubGlobal('confirm', confirm);
    fakeApi({ 'GET /api/v1/sftp/settings': () => json(settings), 'PUT /api/v1/sftp/settings': put });
    renderWithClient(<SftpSettingsSection />);
    const user = userEvent.setup();

    const port = await screen.findByLabelText('Port');
    await user.clear(port);
    await user.type(port, '2200');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('Every SFTP connection ends now'));
    expect(put).not.toHaveBeenCalled();
  });

  it('asks before turning SFTP off', async () => {
    const put = vi.fn(async (request: Request) => {
      expect(await request.json()).toMatchObject({ enabled: false });
      return json({ ...settings, enabled: false, listening: false });
    });
    const confirm = vi.fn(() => true);
    vi.stubGlobal('confirm', confirm);
    fakeApi({
      'GET /api/v1/sftp/settings': () => json(settings),
      'GET /api/v1/sftp': () => json(connection),
      'GET /api/v1/audit': () => json({ items: [], nextCursor: null }),
      'PUT /api/v1/sftp/settings': put,
    });
    renderWithClient(<SftpSettingsSection />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('checkbox', { name: 'SFTP is on' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('Turn SFTP off?'));
    expect(await screen.findByText('Off')).toBeInTheDocument();
  });

  it('does not ask when turning SFTP on', async () => {
    const confirm = vi.fn(() => false);
    vi.stubGlobal('confirm', confirm);
    const put = vi.fn(async () => json(settings));
    fakeApi({
      'GET /api/v1/sftp/settings': () => json({ ...settings, enabled: false, listening: false }),
      'GET /api/v1/sftp': () => json(connection),
      'GET /api/v1/audit': () => json({ items: [], nextCursor: null }),
      'PUT /api/v1/sftp/settings': put,
    });
    renderWithClient(<SftpSettingsSection />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('checkbox', { name: 'SFTP is on' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(put).toHaveBeenCalledOnce());
    expect(confirm).not.toHaveBeenCalled();
  });

  it('refuses a port out of range before asking the panel', async () => {
    const put = vi.fn(async () => json(settings));
    fakeApi({ 'GET /api/v1/sftp/settings': () => json(settings), 'PUT /api/v1/sftp/settings': put });
    renderWithClient(<SftpSettingsSection />);
    const user = userEvent.setup();

    const port = await screen.findByLabelText('Port');
    await user.clear(port);
    await user.type(port, '70000');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('The SFTP port must be between 1 and 65535.')).toBeInTheDocument();
    expect(put).not.toHaveBeenCalled();
  });

  it("translates the panel's errors", async () => {
    vi.stubGlobal('confirm', () => true);
    fakeApi({
      'GET /api/v1/sftp/settings': () => json(settings),
      'PUT /api/v1/sftp/settings': () => problem(400, 'sftp.invalid_host'),
    });
    renderWithClient(<SftpSettingsSection />);
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Public host'), 'https://x');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText(/Give a host name or an IP address/)).toBeInTheDocument();
  });
});
