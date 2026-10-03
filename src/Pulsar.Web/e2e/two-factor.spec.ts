import { expect, test } from '@playwright/test';
import { signIn, signInAsAdministrator, watchCspViolations } from './session';
import { freshTotp, timeStep } from './totp';

// Phase 9: a role that requires two-factor authentication, its user setting it up with an authenticator app (the
// codes computed here, as the app would), signing in with a code and with a recovery code, and an administrator turning
// it off for a user who lost their phone.

test.describe.configure({ mode: 'serial' });

const suffix = Date.now().toString(36);
const roleName = `Operators ${suffix}`;
const operator = { userName: `operator-${suffix}`, password: 'operator password for e2e' };

test('a role requires two-factor authentication and its user signs in with codes', async ({ page }) => {
  test.setTimeout(240_000);
  const cspViolations = watchCspViolations(page);
  await signInAsAdministrator(page);

  // A role with two-factor authentication required, and a user holding it.
  await page.getByRole('link', { name: 'Roles' }).click();
  await page.getByRole('button', { name: 'Create role' }).click();
  const roleDialog = page.getByRole('dialog', { name: 'Create role' });
  await roleDialog.getByLabel('Name').fill(roleName);
  await roleDialog.getByLabel('Scope').selectOption('global');
  await roleDialog.getByLabel('View servers').check();
  await roleDialog.getByRole('button', { name: 'Save' }).click();
  await expect(roleDialog).toBeHidden();
  const require = page.getByLabel(`Require two-factor authentication from users of ${roleName}`);
  await require.check();
  await expect(require).toBeChecked();

  await page.getByRole('link', { name: 'Users' }).click();
  await page.getByRole('button', { name: 'Add user' }).click();
  const userDialog = page.getByRole('dialog', { name: 'Add user' });
  await userDialog.getByLabel('User name').fill(operator.userName);
  await userDialog.getByLabel('Password').fill(operator.password);
  await userDialog.getByLabel(roleName).check();
  await userDialog.getByRole('button', { name: 'Create' }).click();
  await expect(userDialog).toBeHidden();
  await page.getByRole('button', { name: 'Sign out' }).click();

  // Signing in, the operator can only set it up.
  await signIn(page, operator.userName, operator.password);
  await expect(page.getByRole('heading', { name: 'Set up two-factor authentication' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toHaveCount(0);
  expect(await page.evaluate(async () => (await fetch('/api/v1/servers')).status)).toBe(403);

  await page.getByRole('button', { name: 'Set up' }).click();
  await expect(page.getByRole('img', { name: 'QR code for your authenticator app' })).toBeVisible();
  const key = (await page.getByText(/^[A-Z2-7]{4}( [A-Z2-7]{1,4})+$/).textContent()) ?? '';
  await page.screenshot({ path: 'test-results/screens/two-factor-setup.png', fullPage: true });
  let used = await freshTotp(key, timeStep() - 1);
  await page.getByLabel('Code from the app').fill(used.code);
  await page.getByRole('button', { name: 'Turn on' }).click();

  const codes = page.getByRole('list', { name: 'Recovery codes' }).getByRole('listitem');
  await expect(codes).toHaveCount(10);
  const recoveryCode = (await codes.first().textContent()) ?? '';
  await page.screenshot({ path: 'test-results/screens/two-factor-codes.png', fullPage: true });
  await page.getByRole('button', { name: 'I saved them' }).click();
  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
  await page.getByRole('button', { name: 'Sign out' }).click();

  // Password, then a code from the app (a new one: each code works once).
  await signIn(page, operator.userName, operator.password);
  await expect(page.getByRole('heading', { name: 'Two-factor authentication' })).toBeVisible();
  used = await freshTotp(key, used.step);
  await page.getByLabel('Authentication code').fill(used.code);
  await page.getByRole('button', { name: 'Verify' }).click();
  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
  await page.getByRole('button', { name: 'Sign out' }).click();

  // Password, then a recovery code; the same code is refused the second time.
  for (const expected of ['signed in', 'refused']) {
    await signIn(page, operator.userName, operator.password);
    await page.getByRole('button', { name: 'Use a recovery code' }).click();
    await page.getByLabel('Recovery code').fill(recoveryCode);
    await page.getByRole('button', { name: 'Verify' }).click();
    if (expected === 'signed in') {
      await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
      await page.getByRole('button', { name: 'Sign out' }).click();
    } else {
      await expect(page.getByRole('alert')).toHaveText('The code is not valid.');
    }
  }

  // The operator lost their phone: an administrator turns it off; the role makes them set it up again.
  await page.goto('/login');
  await signInAsAdministrator(page);
  await page.getByRole('link', { name: 'Users' }).click();
  const row = page.getByRole('row', { name: new RegExp(operator.userName) });
  await expect(row).toContainText('Two-factor');
  await row.getByRole('button', { name: 'Edit' }).click();
  page.once('dialog', (dialog) => void dialog.accept());
  await page.getByRole('button', { name: 'Turn off two-factor' }).click();
  await expect(page.getByText('Two-factor authentication turned off.')).toBeVisible();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Sign out' }).click();

  await signIn(page, operator.userName, operator.password);
  await expect(page.getByRole('heading', { name: 'Set up two-factor authentication' })).toBeVisible();

  expect(cspViolations).toEqual([]);
});
