import { QueryClient, useQuery, useQueryClient } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useContext, useEffect, useState } from 'react';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../api/client';
import { sessionKey, useCurrentUser, type Session } from '../features/auth/api';
import { RequireSession } from '../features/auth/guards';
import { serverKeys, useSetRconPassword } from '../features/servers/api';
import { adminUser, fakeApi, json } from '../test/fakeApi';
import { App } from './App';
import { initI18n } from './i18n';
import { SessionIdentityBoundary } from './sessionIdentity';

const serverId = '0192f1a0-0000-7000-8000-0000000000e2';
const privateMarker = 'account-A-settings-only';
const settingsA: Schemas['ServerSettings'] = {
  variables: [],
  extraArguments: [privateMarker],
  allowExtraArguments: true,
  autoStart: false,
  restartOnCrash: true,
  hasRcon: true,
  rconPasswordChosen: true,
};

function IdentityProbe({
  original,
  obsoleteSession,
  onClient,
}: {
  original: QueryClient;
  obsoleteSession: Session;
  onClient: (client: QueryClient) => void;
}) {
  const user = useCurrentUser();
  const client = useQueryClient();
  const changeIdentity = useContext(SessionIdentityBoundary);
  const save = useSetRconPassword();
  const [draft, setDraft] = useState('');
  const settings = useQuery<Schemas['ServerSettings'] | null>({
    queryKey: serverKeys.settings(serverId),
    queryFn: () => null,
    enabled: false,
  });
  useEffect(() => {
    onClient(client);
  }, [client, onClient]);
  return (
    <>
      <output aria-label="Current account">{user.id}</output>
      <output aria-label="Settings">{settings.data?.extraArguments.join(' ') ?? 'none'}</output>
      <label>
        Draft
        <input value={draft} onChange={(event) => setDraft(event.target.value)} />
      </label>
      <button onClick={() => save.mutate({ id: serverId, password: 'a-test-password' })}>Save settings</button>
      <button onClick={() => changeIdentity?.(original, obsoleteSession)}>Obsolete transition</button>
    </>
  );
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('session identity boundary', () => {
  it('isolates a late old-account mutation and rejects another transition from its detached client', async () => {
    let current = adminUser;
    let finishMutation: ((response: Response) => void) | undefined;
    fakeApi({
      'GET /api/v1/auth/session': () => json({ setupRequired: false, user: current, insecureConnection: false }),
      [`PUT /api/v1/servers/${serverId}/rcon-password`]: () =>
        new Promise<Response>((resolve) => {
          finishMutation = resolve;
        }),
    });

    const original = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    let activeClient: QueryClient | undefined;
    function currentClient(): QueryClient {
      if (!activeClient) {
        throw new Error('The identity probe did not publish its QueryClient.');
      }
      return activeClient;
    }
    const obsoleteSession: Session = {
      setupRequired: false,
      insecureConnection: false,
      user: { ...adminUser, id: 'obsolete-account-C', userName: 'obsolete-account-C' },
    };

    const router = createMemoryRouter(
      [
        {
          element: <RequireSession />,
          children: [
            {
              path: '/',
              element: (
                <IdentityProbe
                  original={original}
                  obsoleteSession={obsoleteSession}
                  onClient={(client) => {
                    activeClient = client;
                  }}
                />
              ),
            },
          ],
        },
      ],
      { initialEntries: ['/'] },
    );
    render(<App router={router} queryClient={original} />);
    const user = userEvent.setup();
    await waitFor(() => expect(screen.getByLabelText('Current account')).toHaveTextContent(adminUser.id));
    await user.type(screen.getByLabelText('Draft'), 'unsaved A draft');
    await user.click(screen.getByRole('button', { name: 'Save settings' }));
    await waitFor(() => expect(finishMutation).toBeTypeOf('function'));

    // Refreshing the same account preserves the client, mounted observers, and local edits.
    await act(async () => {
      await original.invalidateQueries({ queryKey: sessionKey });
    });
    expect(currentClient()).toBe(original);
    expect(screen.getByLabelText('Draft')).toHaveValue('unsaved A draft');

    current = { ...adminUser, id: 'account-B', userName: 'account-B' };
    await act(async () => {
      await original.invalidateQueries({ queryKey: sessionKey });
    });
    await waitFor(() => expect(screen.getByLabelText('Current account')).toHaveTextContent('account-B'));
    expect(currentClient()).not.toBe(original);
    const accountBClient = currentClient();
    expect(screen.getByLabelText('Draft')).toHaveValue('');
    expect(original.getQueryData<Session>(sessionKey)?.user?.id).toBe(adminUser.id);
    expect(original.getMutationCache().getAll()).toHaveLength(0);

    // This is a real hook-level onSuccess. It still runs after MutationCache.clear() and component unmount.
    await act(async () => {
      if (!finishMutation) {
        throw new Error('The settings mutation did not start.');
      }
      finishMutation(json(settingsA));
    });
    await waitFor(() => expect(original.getQueryData(serverKeys.settings(serverId))).toEqual(settingsA));
    expect(accountBClient.getQueryData(serverKeys.settings(serverId))).toBeUndefined();
    expect(screen.getByLabelText('Settings')).toHaveTextContent('none');
    expect(screen.queryByText(privateMarker)).not.toBeInTheDocument();

    // An obsolete refresh retaining source A cannot swap B into another client or publish account C.
    await user.click(screen.getByRole('button', { name: 'Obsolete transition' }));
    expect(currentClient()).toBe(accountBClient);
    expect(screen.getByLabelText('Current account')).toHaveTextContent('account-B');
    expect(accountBClient.getQueryData<Session>(sessionKey)?.user?.id).toBe('account-B');
  });
});
