import { useState } from 'react';
import type { TemplateVariable } from './api';

/** Values by variable key, in the API's text form ("10", "true", "competitive"). */
export type VariableValues = Record<string, string>;

/** The manifest default, or what a field starts with when there is none. */
export function initialValue(variable: TemplateVariable): string {
  return variable.default ?? (variable.type === 'boolean' ? 'false' : '');
}

/**
 * The integer fields whose text the browser cannot read as a number ("1e"): it reports them as empty, which would put
 * the variable back to its default, so they are refused instead (variableError's `unreadable`).
 */
export function useUnreadableNumbers() {
  const [keys, setKeys] = useState<ReadonlySet<string>>(new Set());
  return {
    keys,
    reset: () => setKeys(new Set()),
    mark: (key: string, unreadable: boolean) =>
      setKeys((current) => {
        if (current.has(key) === unreadable) {
          return current;
        }
        const next = new Set(current);
        if (unreadable) {
          next.add(key);
        } else {
          next.delete(key);
        }
        return next;
      }),
  };
}

/**
 * Checks a value as far as the browser can (the API checks again, with the manifest's own rules): required, range and
 * length. Returns a translation key or null. The pattern is the API's alone: it matches it without backtracking, and
 * the browser's backtracking engine could freeze the page on a nested quantifier; a value that does not match comes
 * back as server.invalid_settings when saving.
 */
export function variableError(
  variable: TemplateVariable,
  value: string,
  secretIsSet = false,
  unreadable = false,
): string | null {
  if (unreadable) {
    return 'servers.variables.notANumber';
  }
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
