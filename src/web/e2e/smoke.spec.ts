import { expect, test } from '@playwright/test';
import { login, logout } from './support/app';

// The session cookie is "__Host-…; Secure". This proves that signing in really works on the base URL
// under test (also through the dev-server proxy on http://localhost) before the longer flows run.
test('oturum açma, kabuk ve çıkış gerçek API ile çalışır', async ({ page }) => {
  await login(page, 'satis1');
  await expect(page.getByRole('navigation', { name: 'Ana gezinme' })).toBeVisible();
  await expect(page).toHaveURL(/\/satis\/siparisler/);
  await expect(page.getByRole('heading', { name: 'Satış siparişleri', level: 1 })).toBeVisible();

  const cookies = await page.context().cookies();
  const session = cookies.find((c) => c.name === '__Host-kantar-session');
  expect(session?.secure, 'oturum çerezi Secure olmalı').toBe(true);
  expect(session?.httpOnly, 'oturum çerezi HttpOnly olmalı').toBe(true);

  // Survives a reload: the session is in the cookie, not in page memory.
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Satış siparişleri', level: 1 })).toBeVisible();

  await logout(page);
});
