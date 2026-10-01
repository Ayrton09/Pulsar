import { expect, test } from '@playwright/test';

test('panel serves the SPA', async ({ page }) => {
  await page.goto('/');

  await expect(page.getByRole('heading', { name: 'Pulsar' })).toBeVisible();
});

test('client-side routes fall back to the SPA', async ({ page }) => {
  await page.goto('/servers/some-id/console');

  await expect(page.getByRole('heading', { name: 'Pulsar' })).toBeVisible();
});

test('panel liveness endpoint responds', async ({ request }) => {
  const response = await request.get('/health/live');

  expect(response.status()).toBe(200);
});
