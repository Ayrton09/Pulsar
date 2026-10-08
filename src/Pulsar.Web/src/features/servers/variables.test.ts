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
  it('matches the pattern against the whole value, as the panel does', () => {
    expect(variableError(map, 'de dust2')).toBeNull();
    expect(variableError(map, 'ok"; rcon_password x')).toBe('servers.variables.badFormat');
    expect(variableError({ ...map, pattern: '^[a-z]+$' }, 'abc')).toBeNull();
  });
});
