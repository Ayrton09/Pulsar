import { QueryClient } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, problem } from '../../test/fakeApi';

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<App router={router} queryClient={queryClient} />);
  return { router, queryClient };
}

async function body(request: Request): Promise<Record<string, unknown>> {
  return (await request.json()) as Record<string, unknown>;
}

const codes = ['ABCDE-FGHIJ', 'KLMNO-PQRST', 'UVWXY-23456', 'BCDFG-HJKMN', 'PQRTV-WXY23'];

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('two-factor sign-in', () => {
  it('asks for the authenticator code after the password', async () => {
    let signedIn = false;
    const sent: Record<string, unknown>[] = [];
    fakeApi({
      'GET /api/v1/auth/session': () =>
        json({ setupRequired: false, user: signedIn ? adminUser : null, insecureConnection: false }),
      'POST /api/v1/auth/login': () => json({ twoFactorRequired: true, user: null }),
      'POST /api/v1/auth/login/two-factor': async (request) => {
        const code = await body(request);
        sent.push(code);
        if (code['code'] !== '123456') {
          return problem(401, 'auth.invalid_code');
        }
        signedIn = true;
        return json(adminUser);
      },
    });
    renderAt('/login');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('User name'), 'admin');
    await user.type(screen.getByLabelText('Password'), 'correct horse battery staple');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    const code = await screen.findByLabelText('Authentication code');
    await user.type(code, '654321');
    await user.click(screen.getByRole('button', { name: 'Verify' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('The code is not valid.');

    await user.clear(code);
    await user.type(code, '123 456');
    await user.click(screen.getByRole('button', { name: 'Verify' }));

    expect(await screen.findByText('Welcome, Ada Admin.')).toBeInTheDocument();
    expect(sent.at(-1)).toEqual({ code: '123456', recoveryCode: null });
  });

  it('takes a recovery code instead', async () => {
    let signedIn = false;
    let sent: Record<string, unknown> = {};
    fakeApi({
      'GET /api/v1/auth/session': () =>
        json({ setupRequired: false, user: signedIn ? adminUser : null, insecureConnection: false }),
      'POST /api/v1/auth/login': () => json({ twoFactorRequired: true, user: null }),
      'POST /api/v1/auth/login/two-factor': async (request) => {
        sent = await body(request);
        signedIn = true;
        return json(adminUser);
      },
    });
    renderAt('/login');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('User name'), 'admin');
    await user.type(screen.getByLabelText('Password'), 'correct horse battery staple');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    await user.click(await screen.findByRole('button', { name: 'Use a recovery code' }));
    await user.type(screen.getByLabelText('Recovery code'), 'abcde-fghij');
    await user.click(screen.getByRole('button', { name: 'Verify' }));

    expect(await screen.findByText('Welcome, Ada Admin.')).toBeInTheDocument();
    expect(sent).toEqual({ code: null, recoveryCode: 'abcde-fghij' });
  });
});

describe('two-factor setup', () => {
  it('makes a user whose role requires it set it up before anything else', async () => {
    let enabled = false;
    let acknowledged = false;
    fakeApi({
      'GET /api/v1/auth/session': () =>
        json({
          setupRequired: false,
          user: { ...adminUser, twoFactorEnabled: enabled, twoFactorSetupRequired: !(enabled && acknowledged) },
          insecureConnection: false,
        }),
      'GET /api/v1/auth/two-factor': () => json({ enabled, required: true, recoveryCodesLeft: enabled ? 10 : 0 }),
      'GET /api/v1/account/api-keys': () => json([]),
      'POST /api/v1/auth/two-factor/setup': async (request) => {
        expect(await body(request)).toEqual({ password: 'my password here' });
        return json({
          sharedKey: 'JBSW Y3DP EHPK 3PXP',
          authenticatorUri: 'otpauth://totp/Pulsar:admin?secret=JBSWY3DPEHPK3PXP&issuer=Pulsar&digits=6',
        });
      },
      'POST /api/v1/auth/two-factor/enable': async (request) => {
        expect(await body(request)).toEqual({ code: '123456', password: 'my password here' });
        enabled = true;
        return json({ recoveryCodes: codes });
      },
    });
    renderAt('/servers');
    const user = userEvent.setup();

    expect(await screen.findByRole('heading', { name: 'Set up two-factor authentication' })).toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'Main navigation' })).not.toBeInTheDocument();

    // Setting it up (a new key) and turning it on take the password: a stolen session alone can do neither.
    expect(screen.getByRole('button', { name: 'Set up' })).toBeDisabled();
    await user.type(screen.getByLabelText('Current password'), 'my password here');
    await user.click(screen.getByRole('button', { name: 'Set up' }));
    expect(await screen.findByRole('img', { name: 'QR code for your authenticator app' })).toBeInTheDocument();
    expect(screen.getByText('JBSW Y3DP EHPK 3PXP')).toBeInTheDocument();

    expect(screen.getByRole('button', { name: 'Turn on' })).toBeDisabled();
    await user.type(screen.getByLabelText('Code from the app'), '123456');
    await user.click(screen.getByRole('button', { name: 'Turn on' }));

    // The codes stay on screen until the user says they saved them.
    expect(await screen.findByText('ABCDE-FGHIJ')).toBeInTheDocument();
    acknowledged = true;
    await user.click(screen.getByRole('button', { name: 'I saved them' }));

    expect(await screen.findByRole('navigation', { name: 'Main navigation' })).toBeInTheDocument();
  });

  it('shows the recovery codes when it is turned on from the account page', async () => {
    let enabled = false;
    fakeApi({
      'GET /api/v1/auth/session': () =>
        json({ setupRequired: false, user: { ...adminUser, twoFactorEnabled: enabled }, insecureConnection: false }),
      'GET /api/v1/auth/two-factor': () => json({ enabled, required: false, recoveryCodesLeft: enabled ? 5 : 0 }),
      'GET /api/v1/account/api-keys': () => json([]),
      'POST /api/v1/auth/two-factor/setup': async (request) => {
        expect(await body(request)).toEqual({ password: 'my password here' });
        return json({
          sharedKey: 'JBSW Y3DP EHPK 3PXP',
          authenticatorUri: 'otpauth://totp/Pulsar:admin?secret=JBSWY3DPEHPK3PXP&issuer=Pulsar&digits=6',
        });
      },
      'POST /api/v1/auth/two-factor/enable': async (request) => {
        expect(await body(request)).toEqual({ code: '123456', password: 'my password here' });
        enabled = true;
        return json({ recoveryCodes: codes });
      },
    });
    const { queryClient } = renderAt('/account');
    const user = userEvent.setup();

    // The password asks for a key.
    const setUp = await screen.findByRole('button', { name: 'Set up' });
    await user.type(
      within(setUp.closest('form') as HTMLElement).getByLabelText('Current password'),
      'my password here',
    );
    await user.click(setUp);
    const appCode = await screen.findByLabelText('Code from the app');
    await user.type(appCode, '123456');
    await user.click(within(appCode.closest('form') as HTMLElement).getByRole('button', { name: 'Turn on' }));

    expect(await screen.findByText('ABCDE-FGHIJ')).toBeInTheDocument();
    // Turning it on closes the user's live channels; the reconnection fetches every visible query again.
    await queryClient.invalidateQueries();
    expect(await screen.findByText('5 recovery codes left.', {}, { timeout: 200 }).catch(() => null)).toBeNull();
    expect(screen.getByText('ABCDE-FGHIJ')).toBeInTheDocument();
    // A browser that refuses the clipboard says so, instead of failing silently.
    vi.spyOn(navigator.clipboard, 'writeText').mockRejectedValue(new Error('denied'));
    await user.click(screen.getByRole('button', { name: 'Copy' }));
    expect(await screen.findByRole('status')).toHaveTextContent('The browser did not allow copying.');
    await user.click(screen.getByRole('button', { name: 'I saved them' }));
    expect(await screen.findByText('5 recovery codes left.')).toBeInTheDocument();
  });

  it('turns it off from the account page with the password', async () => {
    let enabled = true;
    fakeApi({
      'GET /api/v1/auth/session': () =>
        json({ setupRequired: false, user: { ...adminUser, twoFactorEnabled: enabled }, insecureConnection: false }),
      'GET /api/v1/auth/two-factor': () => json({ enabled, required: false, recoveryCodesLeft: 3 }),
      'GET /api/v1/account/api-keys': () => json([]),
      'POST /api/v1/auth/two-factor/disable': async (request) => {
        const { password } = await body(request);
        if (password !== 'my password here') {
          return problem(400, 'auth.wrong_current_password');
        }
        enabled = false;
        return new Response(null, { status: 204 });
      },
    });
    renderAt('/account');
    const user = userEvent.setup();

    expect(await screen.findByText('3 recovery codes left.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Turn off' }));
    const dialog = await screen.findByRole('dialog');
    const password = within(dialog).getByLabelText('Current password');
    await user.type(password, 'not it');
    await user.click(within(dialog).getByRole('button', { name: 'Turn off' }));
    expect(await within(dialog).findByText('The current password is not correct.')).toBeInTheDocument();

    await user.clear(password);
    await user.type(password, 'my password here');
    await user.click(within(dialog).getByRole('button', { name: 'Turn off' }));

    expect(await screen.findByRole('button', { name: 'Set up' })).toBeInTheDocument();
    expect(dialog).not.toBeInTheDocument();
  });

  it('does not offer turning it off while a role requires it', async () => {
    fakeApi({
      'GET /api/v1/auth/session': () =>
        json({ setupRequired: false, user: { ...adminUser, twoFactorEnabled: true }, insecureConnection: false }),
      'GET /api/v1/auth/two-factor': () => json({ enabled: true, required: true, recoveryCodesLeft: 10 }),
      'GET /api/v1/account/api-keys': () => json([]),
    });
    renderAt('/account');

    expect(await screen.findByText('One of your roles requires it.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Turn off' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'New recovery codes' })).toBeInTheDocument();
  });
});
