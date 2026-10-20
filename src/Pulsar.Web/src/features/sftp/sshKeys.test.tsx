import { QueryClient } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, problem, session } from '../../test/fakeApi';
import { sshKeyComment, sshKeyType } from './api';

const line = 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJODCRrRZ4j1+iszm3o/GYD74tq7eiRucOOuCWPu2UAF ada@laptop';
const added: Schemas['SshKeyView'] = {
  id: '0192f1a0-0000-7000-8000-0000000000s1',
  name: 'ada@laptop',
  type: 'ssh-ed25519',
  bits: 256,
  fingerprint: 'SHA256:vkk7w82HhKLd8llWk4tSM08y8S4OwpU/+lEv9zJ9m8s',
  createdAt: '2026-10-19T12:00:00Z',
  lastUsedAt: null,
};

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<App router={router} queryClient={queryClient} />);
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('SSH keys', () => {
  it('names key types and reads the comment of a pasted key', () => {
    expect(sshKeyType('ssh-ed25519', 256)).toBe('Ed25519');
    expect(sshKeyType('ecdsa-sha2-nistp384', 384)).toBe('ECDSA P-384');
    expect(sshKeyType('ssh-rsa', 4096)).toBe('RSA 4096');
    expect(sshKeyComment(`  ${line}\n`)).toBe('ada@laptop');
    expect(sshKeyComment('ssh-ed25519\tAAAA\tmy  work laptop')).toBe('my  work laptop');
    expect(sshKeyComment('ssh-ed25519 AAAA')).toBe('');
  });

  it('says how to make a key, adds one with the password and deletes it', async () => {
    let keys: Schemas['SshKeyView'][] = [];
    let sent: unknown;
    let deleted: string | null = null;
    fakeApi({
      'GET /api/v1/auth/session': session(adminUser),
      'GET /api/v1/auth/two-factor': () => json({ enabled: false, required: false, recoveryCodesLeft: 0 }),
      'GET /api/v1/account/api-keys': () => json([]),
      'GET /api/v1/account/ssh-keys': () => json(keys),
      'POST /api/v1/account/ssh-keys': async (request) => {
        sent = await request.json();
        keys = [added];
        return json(added, 201);
      },
      [`DELETE /api/v1/account/ssh-keys/${added.id}`]: (request) => {
        deleted = new URL(request.url).pathname;
        keys = [];
        return new Response(null, { status: 204 });
      },
    });
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderAt('/account');
    const user = userEvent.setup();

    expect(await screen.findByText('You have no SSH keys.')).toBeInTheDocument();
    expect(screen.getByText('ssh-keygen -t ed25519')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Add key' }));
    const dialog = await screen.findByRole('dialog');

    // No code is asked of a user without two-factor authentication; the key's comment is the name it would get.
    expect(within(dialog).queryByLabelText('Code from the app')).not.toBeInTheDocument();
    await user.click(within(dialog).getByLabelText('Public key'));
    await user.paste(line);
    expect(within(dialog).getByLabelText('Name')).toHaveAttribute('placeholder', 'ada@laptop');
    const submit = within(dialog).getByRole('button', { name: 'Add key' });
    expect(submit).toBeDisabled();
    await user.type(within(dialog).getByLabelText('Current password'), 'my password');
    await user.click(submit);

    const list = await screen.findByRole('list', { name: 'SSH keys' });
    expect(sent).toEqual({ name: null, publicKey: line, password: 'my password', code: null });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(within(list).getByText('ada@laptop')).toBeInTheDocument();
    expect(within(list).getByText('Ed25519')).toBeInTheDocument();
    expect(within(list).getByText(added.fingerprint)).toBeInTheDocument();
    expect(within(list).getByText(/never used/)).toBeInTheDocument();

    await user.click(within(list).getByRole('button', { name: 'Delete' }));
    expect(window.confirm).toHaveBeenCalledWith(expect.stringContaining('ada@laptop'));
    expect(await screen.findByText('You have no SSH keys.')).toBeInTheDocument();
    expect(deleted).toBe(`/api/v1/account/ssh-keys/${added.id}`);
  });

  it('asks a two-factor user for the code and says what the panel refused', async () => {
    const sent: unknown[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, twoFactorEnabled: true }),
      'GET /api/v1/auth/two-factor': () => json({ enabled: true, required: false, recoveryCodesLeft: 8 }),
      'GET /api/v1/account/api-keys': () => json([]),
      'POST /api/v1/account/ssh-keys': async (request) => {
        sent.push(await request.json());
        return sent.length === 1 ? problem(400, 'auth.invalid_code') : problem(400, 'ssh_key.private_key');
      },
    });
    renderAt('/account');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Add key' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByLabelText('Public key'));
    await user.paste(line);
    await user.type(within(dialog).getByLabelText('Name'), 'Laptop');
    await user.type(within(dialog).getByLabelText('Current password'), 'my password');
    const submit = within(dialog).getByRole('button', { name: 'Add key' });
    expect(submit).toBeDisabled();
    expect(within(dialog).getByText(/Recovery codes do not work here/)).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText('Code from the app'), '123 456');
    await user.click(submit);

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('The code is not valid.');
    expect(sent[0]).toEqual({ name: 'Laptop', publicKey: line, password: 'my password', code: '123456' });
    await user.click(submit);
    expect(await within(dialog).findByText(/This is a private key/)).toBeInTheDocument();
  });
});
