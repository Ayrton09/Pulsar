import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { useCallback, useState } from 'react';
import { createBrowserRouter, type DataRouter } from 'react-router';
import { RouterProvider } from 'react-router/dom';
import type { Schemas } from '../api/client';
import { sessionKey } from '../features/auth/api';
import { createQueryClient } from './queryClient';
import { routes } from './routes';
import { SessionIdentityBoundary } from './sessionIdentity';

export interface AppProps {
  /** Tests inject a memory router and a fresh cache. */
  router?: DataRouter;
  queryClient?: QueryClient;
}

export function App({ router, queryClient }: AppProps) {
  const [{ client, generation }, setClient] = useState(() => ({
    client: queryClient ?? createQueryClient(),
    generation: 0,
  }));
  const [appRouter] = useState(() => router ?? createBrowserRouter(routes));
  const changeIdentity = useCallback((source: QueryClient, next: Schemas['SessionResponse']) => {
    setClient((current) => {
      // A late callback from a detached account must not replace the current account's cache again.
      if (current.client !== source) {
        return current;
      }
      const replacement = createQueryClient();
      replacement.setDefaultOptions(source.getDefaultOptions());
      replacement.setQueryData(sessionKey, next);
      return { client: replacement, generation: current.generation + 1 };
    });
  }, []);
  return (
    <SessionIdentityBoundary value={changeIdentity}>
      <QueryClientProvider key={generation} client={client}>
        <RouterProvider router={appRouter} />
      </QueryClientProvider>
    </SessionIdentityBoundary>
  );
}
