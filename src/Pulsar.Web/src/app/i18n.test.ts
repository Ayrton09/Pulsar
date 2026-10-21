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

describe('interpolated values', () => {
  it('show the characters that would reorder or hide what a message says, instead of acting', async () => {
    await initI18n('en');

    // A name a node, a game or another user chose, in a confirmation: "invoice<U+202E>fdp.exe" cannot read as a pdf.
    const question = i18next.t('servers.deleteConfirm', { name: 'invoice‮fdp.exe' });
    expect(question).toContain('invoice[U+202E]fdp.exe');
    expect(question).not.toContain('‮');
    // Plain names, and the joiners some scripts need, are left alone.
    expect(i18next.t('servers.deleteConfirm', { name: 'Señor‍Server 1' })).toContain('Señor‍Server 1');
  });
});
