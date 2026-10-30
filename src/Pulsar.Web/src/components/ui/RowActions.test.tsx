import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { initI18n } from '../../app/i18n';
import { Button } from './Button';
import { RowActions } from './RowActions';

function screenWide(wide: boolean) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: wide,
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
  }));
}

describe('RowActions', () => {
  beforeAll(() => initI18n('en'));
  afterEach(() => vi.unstubAllGlobals());

  it('shows the actions in the row on a wide screen', () => {
    screenWide(true);
    render(
      <RowActions name="server.cfg">
        <Button size="sm">Rename</Button>
        <Button size="sm">Delete</Button>
      </RowActions>,
    );

    expect(screen.getByRole('button', { name: 'Rename' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Actions for server.cfg' })).not.toBeInTheDocument();
  });

  it('keeps them behind one button on a phone, and closes their sheet once one is chosen', async () => {
    screenWide(false);
    const rename = vi.fn();
    render(
      <RowActions name="server.cfg">
        <Button size="sm" onClick={rename}>
          Rename
        </Button>
        <Button size="sm" disabled>
          Delete
        </Button>
      </RowActions>,
    );
    const user = userEvent.setup();
    expect(screen.queryByRole('button', { name: 'Rename' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Actions for server.cfg' }));
    const sheet = screen.getByRole('dialog', { name: 'server.cfg' });
    expect(sheet).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Rename' }));

    expect(rename).toHaveBeenCalledOnce();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});
