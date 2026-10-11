import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, type RouteObject } from 'react-router';
import { RouterProvider } from 'react-router/dom';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { reloadForNewBuild, reloadPage } from '../../lib/reload';
import { RouteError } from './pages';

vi.mock('../../lib/reload', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../lib/reload')>()),
  reloadForNewBuild: vi.fn(() => false),
  reloadPage: vi.fn(),
}));

/** A page whose code fails to load or to render, under the app's error page. */
function renderFailing(error: unknown) {
  const failing: RouteObject[] = [
    {
      errorElement: <RouteError />,
      children: [
        {
          path: '/',
          lazy: () => Promise.reject(error),
        },
      ],
    },
  ];
  render(<RouterProvider router={createMemoryRouter(failing, { initialEntries: ['/'] })} />);
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.clearAllMocks();
  vi.spyOn(console, 'error').mockRestore();
});

describe('route errors', () => {
  it('are caught by an error page of the app, around every route', () => {
    expect(routes).toHaveLength(1);
    expect(routes[0]?.errorElement).toBeDefined();
  });

  it('reload a tab whose page belongs to a build the panel no longer serves', async () => {
    vi.mocked(reloadForNewBuild).mockReturnValue(true);

    renderFailing(new TypeError('Failed to fetch dynamically imported module: /assets/NodesPage-1a2b3c.js'));

    expect(await screen.findByRole('heading', { name: 'The panel was updated' })).toBeInTheDocument();
    expect(reloadForNewBuild).toHaveBeenCalledOnce();
  });

  it('offer to reload when reloading already did not help', async () => {
    renderFailing(new TypeError('Failed to fetch dynamically imported module: /assets/NodesPage-1a2b3c.js'));

    expect(await screen.findByRole('heading', { name: 'The panel was updated' })).toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Reload' }));
    expect(reloadPage).toHaveBeenCalledOnce();
  });

  it('say something went wrong, in the language of the interface, for other errors', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});

    renderFailing(new Error('A bug.'));

    expect(await screen.findByRole('heading', { name: 'Something went wrong' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reload' })).toBeInTheDocument();
    expect(reloadForNewBuild).not.toHaveBeenCalled();
  });
});
