import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query';
import { ApiError } from '../api/client';
import { sessionKey } from '../features/auth/api';

/**
 * One cache for server state. Client errors (4xx) are not retried: they will not fix themselves.
 * A 401 anywhere means the session ended (expired, disabled user, password changed): reload the session so the
 * route guard sends the user to the login page.
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
    if (error instanceof ApiError && error.status === 401) {
      void client.invalidateQueries({ queryKey: sessionKey });
    }
  }

  return client;
}
