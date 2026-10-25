import { vi } from 'vitest';
import type { Schemas } from '../api/client';

type Handler = (request: Request) => Response | Promise<Response>;

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

export function problem(status: number, code: string, title = code): Response {
  return json({ status, title, code }, status);
}

/** The SFTP server, turned off: what tests about something else get. */
const sftpOff = {
  enabled: false,
  host: 'panel.example.com',
  port: 2022,
  hostKeyFingerprint: 'SHA256:off',
  userName: 'admin',
};

/** Pulsar's updates with nothing newer: what tests about something else get. */
export const noUpdates: Schemas['UpdatesView'] = {
  panelVersion: '1.0.0',
  hasKeys: true,
  checkAutomatically: true,
  checkedAt: null,
  checkError: null,
  available: null,
  nodes: [],
};

/** The public IP certificate, turned off: what tests about something else get. */
export const publicIpCertificateOff: Schemas['PublicIpCertificateResponse'] = {
  enabled: false,
  publicIp: null,
  email: null,
  directory: 'production',
  termsOfServiceUrl: 'https://letsencrypt.org/repository/',
  challengePort: 80,
  listening: false,
  listenError: null,
  certificate: null,
  serving: false,
  renewing: false,
  lastRenewalAt: null,
  lastAttemptAt: null,
  nextAttemptAt: null,
  attemptsLeft: 3,
  lastError: null,
  lastErrorAt: null,
};

/** A game whose updates cannot be checked: what tests about something else get. */
export const noGameUpdates: Schemas['GameUpdateView'] = {
  policy: 'notify',
  whenEmpty: false,
  warningMinutes: 0,
  warningMessage: 'Restarting in {time} for a game update',
  supported: false,
  canWaitForEmpty: false,
  canWarn: false,
  installed: null,
  latest: null,
  available: null,
  checkedAt: null,
  checkErrorCode: null,
  checkError: null,
  checking: false,
  nextCheckAt: null,
  pending: false,
  playersOnline: null,
  running: null,
};

export const adminUser: Schemas['CurrentUserResponse'] = {
  id: '0192f1a0-0000-7000-8000-000000000001',
  userName: 'admin',
  displayName: 'Ada Admin',
  email: null,
  permissions: ['audit.view', 'role.manage', 'user.manage', 'user.view'],
  twoFactorEnabled: false,
  twoFactorSetupRequired: false,
  nodePermissions: [],
  serverPermissions: [],
};

/**
 * Replaces the global fetch with routes keyed by "METHOD /path". Unknown requests fail the test loudly instead of
 * hanging. Returns the mock to inspect calls.
 */
export function fakeApi(routes: Record<string, Handler>) {
  const mock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const url = new URL(request.url);
    const key = `${request.method} ${url.pathname}`;
    // Server and node pages show a usage history, and the nodes page the reusable enrollment tokens; tests about
    // something else get empty ones (and the account page no panel certificate, as over plain HTTP, and no SSH keys,
    // and the game artwork on, the panel's default, and no newer release of Pulsar, and games whose updates are not checked).
    const handler =
      routes[key] ??
      (request.method === 'GET' && /^\/api\/v1\/(servers|nodes)\/[^/]+\/metrics$/.test(url.pathname)
        ? () => json(emptyHistory(url.searchParams.get('range')))
        : request.method === 'GET' && /^\/api\/v1\/servers\/[^/]+\/game-updates$/.test(url.pathname)
          ? () => json(noGameUpdates)
          : key === 'GET /api/v1/enrollment-tokens'
            ? () => json([])
            : key === 'GET /api/v1/sftp'
              ? () => json(sftpOff)
              : key === 'GET /api/v1/sftp/settings'
                ? () => json({ ...sftpOff, publicHost: null, listening: false })
                : key === 'GET /api/v1/public-ip-certificate'
                  ? () => json(publicIpCertificateOff)
                  : key === 'GET /api/v1/panel/certificate'
                    ? () => problem(404, 'panel.certificate_unavailable')
                    : key === 'GET /api/v1/appearance'
                      ? () => json({ steamArtwork: true })
                      : key === 'GET /api/v1/account/ssh-keys'
                        ? () => json([])
                        : key === 'GET /api/v1/updates'
                          ? () => json(noUpdates)
                          : undefined);
    if (!handler) {
      throw new Error(`Unexpected request in test: ${key}`);
    }
    return handler(request);
  });
  vi.stubGlobal('fetch', mock);
  return mock;
}

export function session(user: Schemas['CurrentUserResponse'] | null, setupRequired = false): Handler {
  return () => json({ setupRequired, user, insecureConnection: false });
}

function emptyHistory(range: string | null) {
  return {
    range: range ?? 'hour',
    from: '2026-10-09T10:00:00Z',
    to: '2026-10-09T11:00:00Z',
    stepSeconds: 60,
    points: [],
  };
}
