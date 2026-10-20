import type { QueryClient, QueryFilters } from '@tanstack/react-query';

/**
 * After a change: marks the queries matching each of <paramref name="filters"/> stale and fetches the ones on screen
 * again, and resolves when they have their new answer.
 *
 * A request still in flight for one of them asked before the change, so its answer may not show it. When the query
 * already has data, invalidating starts a new request; when it has none yet (its first request is still running), the
 * invalidation would join the old one and keep its stale answer. Requests in flight are cancelled first so every
 * query is fetched again after the change.
 */
export async function invalidateAfterChange(queryClient: QueryClient, ...filters: QueryFilters[]): Promise<void> {
  await Promise.all(filters.map((filter) => queryClient.cancelQueries(filter)));
  await Promise.all(filters.map((filter) => queryClient.invalidateQueries(filter)));
}
