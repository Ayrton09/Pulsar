import i18next from 'i18next';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { chooseLanguage, initI18n, storedLanguage } from './i18n';

// German's texts cannot be fetched (offline, or the panel was updated since this tab loaded it).
vi.mock('../locales/de/common.json', () => {
  throw new TypeError('Failed to fetch dynamically imported module');
});

describe('document language', () => {
  it('follows the language of the interface, so screen readers read it as such', async () => {
    document.documentElement.lang = 'en';

    await initI18n('es-AR');
    expect(document.documentElement.lang).toBe('es');

    await i18next.changeLanguage('en');
    expect(document.documentElement.lang).toBe('en');
  });

  it('names the variants the panel is written in, so browsers draw Chinese characters in their mainland form', async () => {
    await initI18n('en');

    await i18next.changeLanguage('zh');
    expect(document.documentElement.lang).toBe('zh-CN');
    await i18next.changeLanguage('pt');
    expect(document.documentElement.lang).toBe('pt-BR');
    await i18next.changeLanguage('ja');
    expect(document.documentElement.lang).toBe('ja');

    await i18next.changeLanguage('en');
  });
});

describe('chosen language', () => {
  afterEach(async () => {
    vi.restoreAllMocks();
    localStorage.removeItem('pulsar.language');
    await i18next.changeLanguage('en');
  });

  it('is kept in this browser and wins over the browser language at the next start', async () => {
    await initI18n('en-US');
    await chooseLanguage('ko');

    expect(i18next.language).toBe('ko');
    expect(storedLanguage()).toBe('ko');
    await initI18n('es-AR');
    expect(i18next.language).toBe('ko');
  });

  it('is ignored when this browser keeps a language the panel does not have (any more)', async () => {
    localStorage.setItem('pulsar.language', 'xx');

    expect(storedLanguage()).toBeUndefined();
    await initI18n('fr-CA');
    expect(i18next.language).toBe('fr');
  });

  it('starts in English when its texts cannot be fetched, rather than showing no panel', async () => {
    localStorage.setItem('pulsar.language', 'de');

    await initI18n('es-AR');
    expect(i18next.language).toBe('en');
    expect(i18next.t('account.language.title')).toBe('Language');
    await expect(chooseLanguage('de')).rejects.toThrow();
    expect(i18next.language).toBe('en');
  });

  it('still changes the interface when the browser refuses to keep it (a private window, site data blocked)', async () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('Blocked', 'SecurityError');
    });
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('Blocked', 'SecurityError');
    });

    await initI18n('it-IT');
    expect(i18next.language).toBe('it');
    await chooseLanguage('ja');
    expect(i18next.language).toBe('ja');
    expect(storedLanguage()).toBeUndefined();
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
