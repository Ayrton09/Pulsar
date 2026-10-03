import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query';
import { ApiError } from '../api/client';
import { sessionKey } from '../features/auth/api';

/**
 * One cache for server state. Client errors (4xx) are not retried: they will not fix themselves.
 * A 401 anywhere means the session ended (expired, disabled user, password changed): reload the session so the
 * route guard sends the user to the login page (or to set up the two-factor authentication a role requires).
 */
export function createQueryClient(): QueryClient {
  const client: QueryClient = new QueryClient({
    queryCache: new QueryCache({ onError: handleUnauthorized }),
    mutationCache: new MutationCache({ onError: handleUnauthorized }),
    defaultOptions: {
      queries: {
        retry: (failureCount, error) => !(error instanceof ApiError && error.status < 500) && failureCount < 2,
        refetchOnWindowFocus: false,
        staleTime: 30_000,
      },
    },
  });

  function handleUnauthorized(error: unknown) {
    // Also when a role starts requiring two-factor authentication: the session says so, and the guard asks for it.
    if (error instanceof ApiError && (error.status === 401 || error.code === 'auth.two_factor_required')) {
      void client.invalidateQueries({ queryKey: sessionKey });
    }
  }

  return client;
}
