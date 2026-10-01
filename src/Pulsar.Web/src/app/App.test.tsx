import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { App } from './App.tsx';
import { initI18n, resolveLanguage } from './i18n.ts';

describe('resolveLanguage', () => {
  it.each([
    ['es-AR', 'es'],
    ['es', 'es'],
    ['EN-us', 'en'],
    ['fr-FR', 'en'],
    [undefined, 'en'],
  ])('maps %s to %s', (tag, expected) => {
    expect(resolveLanguage(tag)).toBe(expected);
  });
});

describe('App', () => {
  it('renders the tagline in the browser language', async () => {
    await initI18n('es-ES');

    render(<App />);

    expect(screen.getByRole('heading', { name: 'Pulsar' })).toBeInTheDocument();
    expect(screen.getByText('Administración de servidores de juegos, pensada para Windows.')).toBeInTheDocument();
  });
});
