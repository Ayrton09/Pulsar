import type { TemplateVariable } from './api';

/** Values by variable key, in the API's text form ("10", "true", "competitive"). */
export type VariableValues = Record<string, string>;

/** The manifest default, or what a field starts with when there is none. */
export function initialValue(variable: TemplateVariable): string {
  return variable.default ?? (variable.type === 'boolean' ? 'false' : '');
}

/**
 * Checks a value as far as the browser can (the API checks again, with the manifest's own rules): required, range,
 * length and, when the browser understands it, the pattern. Returns a translation key or null.
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
  if (variable.pattern) {
    try {
      // The whole value, as the panel matches it (anchored or not).
      if (!new RegExp(`^(?:${variable.pattern})$`, 'u').test(value)) {
        return 'servers.variables.badFormat';
      }
    } catch {
      // A .NET pattern the browser cannot compile: the API decides.
    }
  }
  return null;
}
