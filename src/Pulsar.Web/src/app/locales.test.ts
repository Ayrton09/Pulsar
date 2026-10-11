import { describe, expect, it } from 'vitest';
import en from '../locales/en/common.json';
import es from '../locales/es/common.json';

/** Every key path of a locale ("servers.states.online"). */
function keys(value: unknown, prefix = ''): string[] {
  if (typeof value !== 'object' || value === null) {
    return [prefix];
  }
  return Object.entries(value).flatMap(([key, child]) => keys(child, prefix ? `${prefix}.${key}` : key));
}

describe('locales', () => {
  it('translate the same keys in English and Spanish', () => {
    expect(keys(es).sort()).toEqual(keys(en).sort());
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
