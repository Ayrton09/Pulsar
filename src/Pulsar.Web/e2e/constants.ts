/**
 * Shared by playwright.config.ts (passed to the panel as InitialSetup:Token) and the tests. Only for the disposable
 * end-to-end database.
 */
export const E2E_SETUP_TOKEN = 'e2e-setup-token-not-a-secret-0123456789';

/** The administrator the tests create on a fresh database, or sign in with when the database was already set up. */
export const E2E_ADMIN = { userName: 'e2e-admin', password: 'end to end admin password' } as const;
