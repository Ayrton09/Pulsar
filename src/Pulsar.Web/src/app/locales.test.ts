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
});
