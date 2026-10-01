import { expect, test, type Page } from '@playwright/test';
import { E2E_ADMIN, E2E_SETUP_TOKEN } from './constants';

// The Phase 1 "done" scenario (docs/architecture/09): an administrator sets the panel up, creates a user with a
// limited role, that user only sees what the role allows, and everything is audited.

// The tests share one database (and the first one may perform the initial setup): run them in order.
test.describe.configure({ mode: 'serial' });

const suffix = Date.now().toString(36);
const roleName = `Auditors ${suffix}`;
const auditor = {
  userName: `auditor-${suffix}`,
  password: 'auditor password for e2e',
  newPassword: 'auditor changed password',
};

async function signInAsAdministrator(page: Page) {
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

async function signIn(page: Page, userName: string, password: string) {
  await page.getByLabel('User name').fill(userName);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

test('administrator delegates audit access to a limited user', async ({ page }) => {
  // The panel serves a strict CSP; any violation means the UI relies on something it blocks.
  const cspViolations: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error' && /Content Security Policy/i.test(message.text())) {
      cspViolations.push(message.text());
    }
  });
  await signInAsAdministrator(page);
  await page.screenshot({ path: 'test-results/screens/dashboard.png', fullPage: true });

  // A global role that can only read the audit log.
  await page.getByRole('link', { name: 'Roles' }).click();
  await page.getByRole('button', { name: 'Create role' }).click();
  const roleDialog = page.getByRole('dialog', { name: 'Create role' });
  await roleDialog.getByLabel('Name').fill(roleName);
  await roleDialog.getByLabel('Scope').selectOption('global');
  await roleDialog.getByLabel('View the audit log').check();
  await page.screenshot({ path: 'test-results/screens/role-dialog.png', fullPage: true });
  await roleDialog.getByRole('button', { name: 'Save' }).click();
  await expect(roleDialog).toBeHidden();
  await expect(page.getByRole('row', { name: new RegExp(roleName) })).toContainText('1 permission');

  // A user holding only that role.
  await page.getByRole('link', { name: 'Users' }).click();
  await page.getByRole('button', { name: 'Add user' }).click();
  const userDialog = page.getByRole('dialog', { name: 'Add user' });
  await userDialog.getByLabel('User name').fill(auditor.userName);
  await userDialog.getByLabel('Password').fill(auditor.password);
  await userDialog.getByLabel(roleName).check();
  await userDialog.getByRole('button', { name: 'Create' }).click();
  await expect(userDialog).toBeHidden();
  await expect(page.getByRole('row', { name: new RegExp(auditor.userName) })).toContainText(roleName);
  await page.screenshot({ path: 'test-results/screens/users.png', fullPage: true });

  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();

  // The auditor sees the audit log and nothing else of the administration.
  await signIn(page, auditor.userName, auditor.password);
  const nav = page.getByRole('navigation', { name: 'Main navigation' });
  await expect(nav.getByRole('link', { name: 'Audit log' })).toBeVisible();
  await expect(nav.getByRole('link', { name: 'Users' })).toHaveCount(0);
  await expect(nav.getByRole('link', { name: 'Roles' })).toHaveCount(0);

  await nav.getByRole('link', { name: 'Audit log' }).click();
  const log = page.getByRole('table', { name: 'Audit log' });
  await expect(log).toContainText('Created a role');
  await expect(log).toContainText('Created a user');
  await expect(log).toContainText('Assigned a role');
  await page.screenshot({ path: 'test-results/screens/audit.png', fullPage: true });

  // Everyone manages their own password; afterwards only the new one works.
  await page.getByRole('link', { name: /My account/ }).click();
  await page.getByLabel('Current password').fill(auditor.password);
  await page.getByLabel('New password').fill(auditor.newPassword);
  await page.getByLabel('Confirm password').fill(auditor.newPassword);
  await page.getByRole('button', { name: 'Change password' }).click();
  await expect(page.getByText(/^Password changed\./)).toBeVisible();
  await page.screenshot({ path: 'test-results/screens/account.png', fullPage: true });

  // Pages outside the role are refused, and the API agrees.
  await page.goto('/admin/users');
  await expect(page.getByRole('heading', { name: 'Not available' })).toBeVisible();
  const status = await page.evaluate(async () => (await fetch('/api/v1/users')).status);
  expect(status).toBe(403);

  await page.getByRole('button', { name: 'Sign out' }).click();
  await signIn(page, auditor.userName, auditor.password);
  await expect(page.getByRole('alert')).toHaveText('Invalid user name or password.');
  await page.getByLabel('User name').fill('');
  await signIn(page, auditor.userName, auditor.newPassword);
  // Back where the sign-out happened (returnTo), signed in.
  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();

  expect(cspViolations, 'Content Security Policy violations').toEqual([]);
});

test('wrong credentials are rejected with a clear message', async ({ page }) => {
  await page.goto('/login');
  test.skip((await page.getByRole('heading', { level: 1 }).textContent()) !== 'Sign in', 'panel not set up yet');

  await signIn(page, 'nobody', 'certainly not the password');

  await expect(page.getByRole('alert')).toHaveText('Invalid user name or password.');
});
