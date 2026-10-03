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

export const adminUser: Schemas['CurrentUserResponse'] = {
  id: '0192f1a0-0000-7000-8000-000000000001',
  userName: 'admin',
  displayName: 'Ada Admin',
  email: null,
  permissions: ['audit.view', 'role.manage', 'user.manage', 'user.view'],
  twoFactorEnabled: false,
  twoFactorSetupRequired: false,
};

/**
 * Replaces the global fetch with routes keyed by "METHOD /path". Unknown requests fail the test loudly instead of
 * hanging. Returns the mock to inspect calls.
 */
export function fakeApi(routes: Record<string, Handler>) {
  const mock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const key = `${request.method} ${new URL(request.url).pathname}`;
    const handler = routes[key];
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
