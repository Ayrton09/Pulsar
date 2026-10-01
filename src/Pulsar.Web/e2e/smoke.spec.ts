import { expect, test } from '@playwright/test';

test('panel serves the SPA', async ({ page }) => {
  await page.goto('/');

  // Depending on the database: the setup page (fresh) or the sign-in page.
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(/Create the administrator|Sign in/);
});

test('client-side routes fall back to the SPA', async ({ page }) => {
  await page.goto('/servers/some-id/console');

  await expect(page).toHaveURL(/\/(login|setup)/);
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
});

test('panel liveness endpoint responds', async ({ request }) => {
  const response = await request.get('/health/live');

  expect(response.status()).toBe(200);
});
