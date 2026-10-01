import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { createBrowserRouter, type DataRouter } from 'react-router';
import { RouterProvider } from 'react-router/dom';
import { createQueryClient } from './queryClient';
import { routes } from './routes';

export interface AppProps {
  /** Tests inject a memory router and a fresh cache. */
  router?: DataRouter;
  queryClient?: QueryClient;
}

export function App({ router, queryClient }: AppProps) {
  const [client] = useState(() => queryClient ?? createQueryClient());
  const [appRouter] = useState(() => router ?? createBrowserRouter(routes));
  return (
    <QueryClientProvider client={client}>
      <RouterProvider router={appRouter} />
    </QueryClientProvider>
  );
}
