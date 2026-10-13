import i18n from 'i18next';
import { beforeAll, describe, expect, it } from 'vitest';
import { ApiError } from '../api/client';
import { initI18n } from '../app/i18n';
import { errorMessage } from './errors';

beforeAll(async () => {
  await initI18n('en-US');
});

describe('errorMessage', () => {
  const t = i18n.t.bind(i18n);

  it('translates the code the API sends', () => {
    expect(
      errorMessage(t, new ApiError(401, { title: 'Invalid user name or password.', code: 'auth.invalid_credentials' })),
    ).toBe('Invalid user name or password.');
    expect(errorMessage(t, new ApiError(0, { title: 'Network error', code: 'http.network' }))).toBe(
      'Could not reach the panel. Check your connection.',
    );
  });

  it("does not show the server's English title for an error it has no code for", () => {
    // An unhandled exception in the panel: ProblemDetails without a code.
    expect(errorMessage(t, new ApiError(500, { title: 'An error occurred while processing your request.' }))).toBe(
      'Something went wrong. Try again.',
    );
  });

  it('falls back to what the API said for a code nobody translated', () => {
    expect(errorMessage(t, new ApiError(400, { title: 'Pick a smaller map.', code: 'maps.too_big' }))).toBe(
      'Pick a smaller map.',
    );
  });

  it('says something went wrong for anything else', () => {
    expect(errorMessage(t, new Error('boom'))).toBe('Something went wrong. Try again.');
  });
});
