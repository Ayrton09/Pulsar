import { QueryClient } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, problem, session } from '../../test/fakeApi';

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
  render(<App router={router} queryClient={queryClient} />);
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

// Whoever manages the panel's settings, on the Games page.
const settingsManager = { ...adminUser, permissions: ['template.view', 'settings.manage'] };

describe('the CurseForge key', () => {
  it('is checked when saved, never shown again, and removed after asking', async () => {
    let configured = false;
    const sent: unknown[] = [];
    let removed = 0;
    fakeApi({
      'GET /api/v1/auth/session': session(settingsManager),
      'GET /api/v1/appearance': () => json({ steamArtwork: true }),
      'GET /api/v1/templates': () => json([]),
      'GET /api/v1/mod-catalogs': () => json({ curseForgeConfigured: configured, updatedAt: null }),
      'PUT /api/v1/mod-catalogs/curseforge-key': async (request) => {
        const body = (await request.json()) as { apiKey: string };
        sent.push(body);
        if (body.apiKey !== 'right-key') {
          return problem(400, 'mods.curseforge_key_rejected', 'CurseForge does not accept that API key.');
        }
        configured = true;
        return json({ curseForgeConfigured: true, updatedAt: '2026-10-26T00:00:00Z' });
      },
      'DELETE /api/v1/mod-catalogs/curseforge-key': () => {
        removed++;
        configured = false;
        return json({ curseForgeConfigured: false, updatedAt: '2026-10-26T00:00:00Z' });
      },
    });
    renderAt('/templates');

    const field = await screen.findByLabelText('CurseForge API key');
    expect(field).toHaveAttribute('type', 'password');
    expect(screen.getByText('Not set')).toBeInTheDocument();
    await userEvent.type(field, 'wrong-key');
    await userEvent.click(screen.getByRole('button', { name: 'Save key' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('CurseForge does not accept that API key.');

    await userEvent.clear(field);
    await userEvent.type(field, '  right-key ');
    await userEvent.click(screen.getByRole('button', { name: 'Save key' }));
    expect(await screen.findByText('Set')).toBeInTheDocument();
    expect(screen.getByLabelText('Replace the CurseForge API key')).toHaveValue(''); // never shown again
    expect(sent.at(-1)).toEqual({ apiKey: 'right-key' });

    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValue(true);
    await userEvent.click(screen.getByRole('button', { name: 'Remove key' }));
    expect(confirm).toHaveBeenCalled();
    expect(removed).toBe(0);
    await userEvent.click(screen.getByRole('button', { name: 'Remove key' }));
    await waitFor(() => expect(removed).toBe(1));
    expect(await screen.findByText('Not set')).toBeInTheDocument();
  });
});
