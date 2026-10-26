import { describe, expect, it, vi } from 'vitest';
import { ApiError, XSRF_HEADER, createApiClient, readCookie, unwrap } from './client';

function jsonResponse(body: unknown, status = 200, contentType = 'application/json') {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': contentType } });
}

describe('readCookie', () => {
  it('finds a cookie among others and decodes it', () => {
    expect(readCookie('b', 'a=1; b=x%3Dy; c=3')).toBe('x=y');
    expect(readCookie('missing', 'a=1')).toBeUndefined();
  });
});

describe('api client', () => {
  it('sends the antiforgery token on state-changing requests only', async () => {
    const fetchMock = vi.fn((request: Request) => Promise.resolve(jsonResponse({ request: request.url }, 200)));
    const client = createApiClient({
      baseUrl: 'http://panel.test',
      fetch: fetchMock as unknown as typeof fetch,
      readToken: () => 'token-123',
    });

    await client.GET('/api/v1/auth/me');
    await client.POST('/api/v1/auth/logout');

    const [get, post] = fetchMock.mock.calls.map(([request]) => request);
    expect(get?.headers.get(XSRF_HEADER)).toBeNull();
    expect(post?.headers.get(XSRF_HEADER)).toBe('token-123');
  });

  it('turns problem details into ApiError with the stable code', async () => {
    const fetchMock = vi.fn(() =>
      Promise.resolve(
        jsonResponse(
          { title: 'Invalid user name or password.', code: 'auth.invalid_credentials' },
          401,
          'application/problem+json',
        ),
      ),
    );
    const client = createApiClient({ baseUrl: 'http://panel.test', fetch: fetchMock as unknown as typeof fetch });

    const error = await unwrap(
      client.POST('/api/v1/auth/login', { body: { userName: 'a', password: 'b', rememberMe: false } }),
    ).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 401, code: 'auth.invalid_credentials' });
  });

  it('keeps field errors of validation problems', async () => {
    const fetchMock = vi.fn(() =>
      Promise.resolve(
        jsonResponse(
          {
            title: 'One or more validation errors occurred.',
            code: 'validation.failed',
            errors: { password: ['Too short'] },
          },
          400,
          'application/problem+json',
        ),
      ),
    );
    const client = createApiClient({ baseUrl: 'http://panel.test', fetch: fetchMock as unknown as typeof fetch });

    const error = (await unwrap(
      client.POST('/api/v1/auth/login', { body: { userName: 'a', password: 'b', rememberMe: false } }),
    ).catch((e: unknown) => e)) as ApiError;

    expect(error.errors).toEqual({ password: ['Too short'] });
  });

  it('reports a request that never reached the panel as a network error', async () => {
    const fetchMock = vi.fn(() => Promise.reject(new TypeError('Failed to fetch')));
    const client = createApiClient({ baseUrl: 'http://panel.test', fetch: fetchMock as unknown as typeof fetch });

    const error = await unwrap(client.GET('/api/v1/auth/session')).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 0, code: 'http.network' });
  });

  it('lets a cancelled request stay cancelled', async () => {
    const fetchMock = vi.fn(() => Promise.reject(new DOMException('Aborted', 'AbortError')));
    const client = createApiClient({ baseUrl: 'http://panel.test', fetch: fetchMock as unknown as typeof fetch });

    const error = await unwrap(client.GET('/api/v1/auth/session')).catch((e: unknown) => e);

    expect(error).not.toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ name: 'AbortError' });
  });
});
