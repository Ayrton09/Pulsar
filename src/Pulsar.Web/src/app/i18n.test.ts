import i18next from 'i18next';
import { describe, expect, it } from 'vitest';
import { initI18n } from './i18n';

describe('document language', () => {
  it('follows the language of the interface, so screen readers read it as such', async () => {
    document.documentElement.lang = 'en';

    await initI18n('es-AR');
    expect(document.documentElement.lang).toBe('es');

    await i18next.changeLanguage('en');
    expect(document.documentElement.lang).toBe('en');
  });
});
