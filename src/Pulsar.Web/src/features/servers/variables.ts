import type { TemplateVariable } from './api';

/** Values by variable key, in the API's text form ("10", "true", "competitive"). */
export type VariableValues = Record<string, string>;

/** The manifest default, or what a field starts with when there is none. */
export function initialValue(variable: TemplateVariable): string {
  return variable.default ?? (variable.type === 'boolean' ? 'false' : '');
}

/**
 * Checks a value as far as the browser can (the API checks again, with the manifest's own rules): required, range and
 * length. Returns a translation key or null. The pattern is the API's alone: it matches it without backtracking, and
 * the browser's backtracking engine could freeze the page on a nested quantifier; a value that does not match comes
 * back as server.invalid_settings when saving.
 */
export function variableError(variable: TemplateVariable, value: string, secretIsSet = false): string | null {
  if (value === '') {
    return variable.required && !secretIsSet ? 'validation.required' : null;
  }
  if (variable.type === 'integer') {
    const number = Number(value);
    if (
      !Number.isInteger(number) ||
      (variable.min != null && number < variable.min) ||
      (variable.max != null && number > variable.max)
    ) {
      return 'servers.variables.outOfRange';
    }
  }
  if (variable.maxLength != null && value.length > variable.maxLength) {
    return 'validation.tooLong';
  }
  return null;
}
