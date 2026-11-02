import { defineConfig, devices } from '@playwright/test';

/**
 * The admin portal, offline: the portal's Vite dev server over the contract
 * suite's throwaway .NET stack, with Google's sign-in stood in for in the
 * browser (`support/google-identity-stand-in.ts`). Run it with
 * `e2e/scripts/admin-offline-test.sh`, which starts both and sets the two
 * variables below.
 *
 * A config of its own, not a third project in `playwright.config.ts`: that
 * config is about deployed environments (`E2E_TARGET`, a seeded password),
 * and this suite needs neither — it holds no credential at all, and runs
 * against nothing that outlives it. Keeping it apart keeps `npx playwright
 * test` meaning exactly what it meant before.
 */

const portalUrl = process.env.E2E_ADMIN_PORTAL_URL;
if (!portalUrl || !process.env.CONTRACT_ADMIN_URL) {
  throw new Error(
    'E2E_ADMIN_PORTAL_URL and CONTRACT_ADMIN_URL are unset — run e2e/scripts/admin-offline-test.sh',
  );
}

export default defineConfig({
  testDir: './tests/admin',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  use: {
    baseURL: portalUrl,
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
    // Nothing secret is typed here, but the rule is the suite's: no trace or
    // screenshot leaves CI (playwright.config.ts owns the reasoning).
    trace: process.env.CI ? 'off' : 'retain-on-failure',
    screenshot: process.env.CI ? 'off' : 'only-on-failure',
    video: 'off',
  },
  reporter: process.env.CI ? [['github'], ['list']] : [['list']],
  projects: [{ name: 'admin', use: { ...devices['Desktop Chrome'] } }],
});
