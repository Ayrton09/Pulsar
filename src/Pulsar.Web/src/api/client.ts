import createClient, { type Middleware } from 'openapi-fetch';
import type { components, paths } from './schema';

/** Types of the panel API, generated from src/api/openapi.json (`npm run api:generate`). */
export type Schemas = components['schemas'];

/**
 * Readable cookie with the antiforgery request token (the __Host- name over HTTPS, the plain one when the panel
 * allows HTTP), and the header the API expects it in.
 */
export const XSRF_COOKIES = ['__Host-pulsar-xsrf', 'pulsar-xsrf'] as const;
export const XSRF_HEADER = 'X-XSRF-TOKEN';

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS', 'TRACE']);

export function readCookie(name: string, cookies: string = document.cookie): string | undefined {
  for (const part of cookies.split(';')) {
    const [key, ...value] = part.trim().split('=');
    if (key === name) {
      return decodeURIComponent(value.join('='));
    }
  }
  return undefined;
}

/** Sends the antiforgery token on every state-changing request (the session cookie travels on its own). */
export function antiforgeryMiddleware(readToken: () => string | undefined): Middleware {
  return {
    onRequest({ request }) {
      if (!SAFE_METHODS.has(request.method)) {
        const token = readToken();
        if (token) {
          request.headers.set(XSRF_HEADER, token);
        }
      }
      return request;
    },
  };
}

/** An error answered by the API: always ProblemDetails with a stable `code` (translated by the UI). */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly errors: Record<string, string[]> | undefined;

  constructor(status: number, problem: { title?: string | null; code?: unknown; errors?: unknown } | undefined) {
    super(problem?.title ?? `Request failed with status ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.code = typeof problem?.code === 'string' ? problem.code : `http.${status}`;
    this.errors = isErrorMap(problem?.errors) ? problem.errors : undefined;
  }
}

function isErrorMap(value: unknown): value is Record<string, string[]> {
  return typeof value === 'object' && value !== null && Object.values(value).every((v) => Array.isArray(v));
}

export interface ApiClientOptions {
  baseUrl?: string;
  fetch?: typeof fetch;
  /** Where the antiforgery token comes from (the cookie by default; tests inject it). */
  readToken?: () => string | undefined;
}

export function createApiClient(options: ApiClientOptions = {}) {
  const client = createClient<paths>({
    baseUrl: options.baseUrl ?? globalThis.location?.origin ?? '',
    credentials: 'same-origin',
    // Resolved on every call (not captured once), so tests can replace the global fetch.
    fetch: options.fetch ?? ((input: Request) => globalThis.fetch(input)),
  });
  client.use(
    antiforgeryMiddleware(options.readToken ?? (() => XSRF_COOKIES.map((name) => readCookie(name)).find(Boolean))),
  );
  return client;
}

export const api = createApiClient();

/** Returns the data of an openapi-fetch call, or throws {@link ApiError}. */
export async function unwrap<T>(call: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await call;
  if (!response.ok) {
    throw new ApiError(response.status, error as ConstructorParameters<typeof ApiError>[1]);
  }
  return data as T;
}
