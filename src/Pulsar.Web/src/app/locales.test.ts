import { describe, expect, it } from 'vitest';
import en from '../locales/en/common.json';
import es from '../locales/es/common.json';
import { supportedLanguages } from './i18n';

// Every locale, as the panel fetches them one at a time.
const locales = import.meta.glob<{ default: typeof en }>('../locales/*/common.json', { eager: true });
const resources = Object.fromEntries(
  Object.entries(locales).map(([path, module]) => [path.split('/').at(-2), { common: module.default }]),
);

/** Every key path of a locale ("servers.states.online"). */
function keys(value: unknown, prefix = ''): string[] {
  if (typeof value !== 'object' || value === null) {
    return [prefix];
  }
  return Object.entries(value).flatMap(([key, child]) => keys(child, prefix ? `${prefix}.${key}` : key));
}

/** The text at a key path. */
function at(locale: unknown, path: string): string {
  return path.split('.').reduce<unknown>((value, key) => (value as Record<string, unknown>)[key], locale) as string;
}

/** The {{placeholders}} of a message, sorted. */
function placeholders(text: string): string[] {
  return [...text.matchAll(/\{\{\s*([^}]+?)\s*\}\}/g)].map((match) => match[1] ?? '').sort();
}

const translations = supportedLanguages.filter((language) => language !== 'en');

describe('locales', () => {
  it('are all there', () => {
    expect(Object.keys(resources).sort()).toEqual([...supportedLanguages].sort());
  });

  it.each(translations)('translate the same keys in %s as in English', (language) => {
    expect(keys(resources[language]?.common)).toEqual(keys(en));
  });

  it.each(translations)('keep every placeholder of a message in %s, and leave none empty', (language) => {
    const locale = resources[language]?.common;
    for (const path of keys(en)) {
      expect.soft(placeholders(at(locale, path)), path).toEqual(placeholders(at(en, path)));
      if (at(en, path).trim() !== '') {
        expect.soft(at(locale, path).trim(), path).not.toBe('');
      }
    }
  });

  it('name the audit actions and errors of mods, limits and template upgrades', () => {
    expect(keys(en)).toEqual(
      expect.arrayContaining([
        'audit.actions.server.addon_install_requested',
        'audit.actions.server.addon_remove_requested',
        'errors.server.limits_unsupported',
        'errors.server.limits_invalid',
        'errors.server.template_not_newer',
      ]),
    );
  });

  it('name the codes operations, backups and refused commands end with', () => {
    expect(keys(en)).toEqual(
      expect.arrayContaining([
        'errors.steamcmd.guard_required',
        'errors.steamcmd.login_failed',
        'errors.steamcmd.failed',
        'errors.operation.interrupted',
        'errors.install.test_game_unavailable',
        'errors.process.start_failed',
        'errors.addon.unknown',
        'errors.addon.download_failed',
        'errors.files.open_failed',
        'errors.console.invalid_input',
        'errors.server.not_running',
        'errors.server.unknown',
        'errors.server.running',
        'errors.server.invalid_spec',
        'errors.server.invalid_short_id',
        'errors.server.spec_mismatch',
        'errors.server.create_forbidden',
        'servers.ackStatuses.rejected',
      ]),
    );
  });

  it('keep the details of a mod refusal in Spanish too', () => {
    for (const code of ['requires', 'conflicts', 'required_by'] as const) {
      expect(es.errors.addon[code]).toContain('{{detail}}');
    }
  });
});
