import { describe, expect, it } from 'vitest';
import type { TemplateVariable } from './api';
import { variableError } from './variables';

const map: TemplateVariable = {
  key: 'map',
  label: 'Map',
  type: 'string',
  default: null,
  required: false,
  secret: false,
  userEditable: true,
  pattern: '[A-Za-z0-9 ]+',
  maxLength: null,
  min: null,
  max: null,
  options: [],
  description: null,
};

describe('variableError', () => {
  it('leaves the pattern to the panel, which matches it without backtracking', () => {
    // A nested quantifier the browser's backtracking engine would take minutes over: not evaluated here.
    const started = performance.now();
    expect(variableError({ ...map, pattern: '(a+)+$' }, `${'a'.repeat(26)}!`)).toBeNull();
    expect(performance.now() - started).toBeLessThan(100);
    expect(variableError(map, 'ok"; rcon_password x')).toBeNull();
  });

  it('checks what does not need the pattern', () => {
    expect(variableError({ ...map, required: true }, '')).toBe('validation.required');
    expect(variableError({ ...map, maxLength: 3 }, 'dust')).toBe('validation.tooLong');
    expect(variableError(map, 'de dust2')).toBeNull();
  });

  it('takes a number as the panel does: a plain decimal in its range, up to 6 decimals', () => {
    const rate: TemplateVariable = { ...map, key: 'rate', type: 'number', pattern: null, min: 0.1, max: 100 };

    for (const value of ['1', '1.5', '0.1', '100', '99.999999', '+2']) {
      expect(variableError(rate, value), value).toBeNull();
    }
    for (const value of ['0.05', '100.5', '1e2', '1,5', '1.1234567', 'NaN', 'Infinity', '.5']) {
      expect(variableError(rate, value), value).toBe('servers.variables.outOfRange');
    }
    // Left empty, an option keeps the game's own value.
    expect(variableError(rate, '')).toBeNull();
  });
});
