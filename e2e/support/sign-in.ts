import { expect, type Page } from '@playwright/test';

import { isCognitoHost, testUser } from './env';

/**
 * Sign in through the Cognito hosted UI and return once the app has taken over.
 *
 * Extracted so a spec whose SUBJECT is something else does not restate the
 * whole auth dance. `tests/auth-round-trip.spec.ts` deliberately does NOT use
 * this: the navigation details this helper skips past — that the redirect went
 * to a Cognito host, that the return leg actually landed on `/auth/callback` —
 * are that spec's entire subject, and asserting them through a helper written
 * for other callers is how a test stops testing what it claims to.
 *
 * The markup notes that make the selectors below safe (managed login, not the
 * classic hosted UI; ids and classes are unstable) are owned by that spec's
 * header comment. Read it before changing anything here.
 *
 * The two `fill()` calls are the only lines in the suite that touch the
 * password. No trace is recorded in CI — see playwright.config.ts.
 */
export async function signIn(page: Page): Promise<string> {
  const { email, password } = testUser();

  await page.goto('/');
  const appOrigin = new URL(page.url()).origin;
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL((url) => isCognitoHost(url.hostname));

  const form = page.locator('form#primary-form');
  await expect(form, 'the Cognito page should show its sign-in form').toBeVisible();
  await form.locator('input[name="username"]').fill(email);
  await form.locator('input[name="password"]').fill(password);
  await form.getByRole('button', { name: 'Sign in' }).click();

  await page.waitForURL(
    (url) => url.origin === appOrigin && !url.pathname.startsWith('/auth/callback'),
    { timeout: 30_000 },
  );
  // The account menu's TRIGGER, not the sign-out control: the identity and the
  // way out moved behind one avatar in the app's header, so this is the thing
  // that is visible without opening anything. `auth-round-trip.spec.ts` owns
  // the contract and exercises the menu itself.
  await expect(page.getByRole('button', { name: 'Account menu' })).toBeVisible();
  return appOrigin;
}
