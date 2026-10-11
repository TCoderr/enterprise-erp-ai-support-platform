import { defineConfig } from '@playwright/test';

// End-to-end tests run against a real API: either the built app served by the API host
// (KANTAR_E2E_BASE_URL, default https://localhost:8963) or the Vite dev server proxying to it.
// KANTAR_E2E_CHANNEL: "chrome" (default, the installed browser) or "chromium" (the bundled one, for CI).
const channel = process.env.KANTAR_E2E_CHANNEL ?? 'chrome';

export default defineConfig({
  testDir: './e2e',
  workers: 1,
  fullyParallel: false,
  retries: 0,
  forbidOnly: true,
  timeout: 120_000,
  expect: { timeout: 10_000 },
  reporter: [['list'], ['json', { outputFile: 'test-results/e2e-results.json' }]],
  outputDir: 'test-results/playwright',
  use: {
    baseURL: process.env.KANTAR_E2E_BASE_URL ?? 'https://localhost:8963',
    ignoreHTTPSErrors: true,
    locale: 'tr-TR',
    timezoneId: 'Europe/Istanbul',
    viewport: { width: 1440, height: 900 },
    trace: 'off',
    video: 'off',
    screenshot: 'only-on-failure',
    ...(channel === 'chromium' ? {} : { channel }),
  },
});
