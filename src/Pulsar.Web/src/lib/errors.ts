import type { TFunction } from 'i18next';
import { ApiError } from '../api/client';

/** User-facing message for an error: the translation of the API's stable code, or a generic message. */
export function errorMessage(t: TFunction, error: unknown): string {
  if (error instanceof ApiError) {
    return t(`errors.${error.code}`, { defaultValue: error.message });
  }
  return t('errors.unexpected');
}

/** First message per field of a validation problem (keys as the API sends them: userName, password…). */
export function fieldErrors(error: unknown): Record<string, string> {
  if (!(error instanceof ApiError) || !error.errors) {
    return {};
  }
  return Object.fromEntries(
    Object.entries(error.errors)
      .filter(([, messages]) => messages.length > 0)
      .map(([field, messages]) => [field, messages[0] as string]),
  );
}
