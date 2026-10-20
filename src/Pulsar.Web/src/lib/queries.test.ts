import { QueryClient, QueryObserver } from '@tanstack/react-query';
import { describe, expect, it } from 'vitest';
import { invalidateAfterChange } from './queries';

describe('invalidateAfterChange', () => {
  it('fetches a query again after the change while its first request is still running', async () => {
    const queryClient = new QueryClient();
    let answerFirst: (answer: string[]) => void = () => {};
    const first = new Promise<string[]>((resolve) => (answerFirst = resolve));
    let requests = 0;
    const observer = new QueryObserver(queryClient, {
      queryKey: ['things'],
      queryFn: () => (requests++ === 0 ? first : Promise.resolve(['before', 'created'])),
    });
    const stop = observer.subscribe(() => {});

    const changed = invalidateAfterChange(queryClient, { queryKey: ['things'] });
    answerFirst(['before']); // read before the change
    await changed;

    expect(observer.getCurrentResult().data).toEqual(['before', 'created']);
    expect(requests).toBe(2);
    stop();
  });

  it('fetches a query that already has data again, and leaves the others alone', async () => {
    const queryClient = new QueryClient();
    let requests = 0;
    const observer = new QueryObserver(queryClient, {
      queryKey: ['things'],
      queryFn: () => Promise.resolve(++requests),
    });
    const stop = observer.subscribe(() => {});
    await observer.refetch();
    const before = requests;
    queryClient.setQueryData(['other'], 'kept');

    await invalidateAfterChange(queryClient, { queryKey: ['things'] });

    expect(requests).toBe(before + 1);
    expect(observer.getCurrentResult().data).toBe(requests);
    expect(queryClient.getQueryState(['other'])?.isInvalidated).toBe(false);
    stop();
  });
});
