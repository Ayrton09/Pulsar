import { expect, type Page } from '@playwright/test';
import { E2E_ADMIN, E2E_SETUP_TOKEN } from './constants';

/** Signs in as the E2E administrator, creating it first on a fresh database. */
export async function signInAsAdministrator(page: Page) {
  await page.goto('/');
  const heading = page.getByRole('heading', { level: 1 });
  await expect(heading).toHaveText(/Create the administrator|Sign in/);

  if ((await heading.textContent()) === 'Create the administrator') {
    await page.getByLabel('Setup token').fill(E2E_SETUP_TOKEN);
    await page.getByLabel('User name').fill(E2E_ADMIN.userName);
    await page.getByLabel('Display name').fill('E2E Admin');
    await page.getByLabel('Password', { exact: true }).fill(E2E_ADMIN.password);
    await page.getByLabel('Confirm password').fill(E2E_ADMIN.password);
    await page.screenshot({ path: 'test-results/screens/setup.png', fullPage: true });
    await page.getByRole('button', { name: 'Create administrator' }).click();
  } else {
    await signIn(page, E2E_ADMIN.userName, E2E_ADMIN.password);
  }

  await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible();
}

export async function signIn(page: Page, userName: string, password: string) {
  await page.getByLabel('User name').fill(userName);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

/** Collects Content-Security-Policy violations: the panel's CSP is strict and the UI must not rely on anything it blocks. */
export function watchCspViolations(page: Page): string[] {
  const violations: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error' && /Content Security Policy/i.test(message.text())) {
      violations.push(message.text());
    }
  });
  return violations;
}
