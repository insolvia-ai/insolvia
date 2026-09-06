import { expect, test } from '@playwright/test';

import {
  baseUrl,
  cognitoClientId,
  cognitoHostDescription,
  environmentLabel,
  expectedBuildStamp,
  isCognitoHost,
} from '../../support/env';

/**
 * The deployed app, from the outside, with no credentials.
 *
 * THIS IS THE ONLY BROWSER TEST THAT RUNS AGAINST PRODUCTION, and it is a
 * detector rather than a gate there: by the time it runs the bundle is
 * already live. What it buys is the difference between a broken deploy
 * announcing itself in minutes with a named cause and being found by a
 * customer. On staging it also runs, ahead of the `flows` project, so the
 * assertions themselves are exercised before they are the last word on prod.
 *
 * WHAT IT PROVES that `curl /` cannot: that the bundle CloudFront serves is
 * THIS run's (the footer's build stamp), that it was built for THIS
 * environment (the footer's label — a staging bundle in the production bucket
 * is the failure Metro's cache once made possible), that the SPA rewrite
 * turns a deep link into the app rather than an S3 error, and that "Sign in"
 * hands off to this environment's own pool with a client id and callback the
 * pool actually registered — all without typing a password anywhere.
 *
 * SELECTOR CONTRACT with the app, role-based by accessible name:
 *   - sign-in trigger : button named "Sign in"
 *   - the footer      : the text "Insolvia · <Label> · <host> · <stamp>"
 * Cognito's page: `form#primary-form` (managed login; the flows spec owns the
 * markup notes).
 */

const signInButton = (page: import('@playwright/test').Page) =>
  page.getByRole('button', { name: 'Sign in' });

test.describe('smoke: the deployed app', () => {
  test('serves this environment’s shell, built from this commit', async ({ page }) => {
    const response = await page.goto('/');
    expect(response?.status(), 'the app origin should answer 200').toBe(200);

    await expect(
      signInButton(page),
      'the signed-out app should render a "Sign in" button — a blank page means ' +
        'the bundle did not execute (a chunk 404 rewritten to index.html, a ' +
        'CSP block, or a broken export)',
    ).toBeVisible();

    const label = environmentLabel();
    await expect(
      page.getByText(new RegExp(`Insolvia · ${label} ·`)),
      `the footer should say "${label}" — a different label means a bundle built ` +
        'for another environment is being served from this bucket',
    ).toBeVisible();

    const stamp = expectedBuildStamp();
    if (stamp !== undefined) {
      await expect(
        page.getByText(new RegExp(`· ${stamp}$`)),
        `the footer should carry build stamp ${stamp} — the commit this run just ` +
          'deployed; an older stamp means CloudFront is still serving the previous ' +
          'bundle (invalidation not propagated) or the sync uploaded nothing',
      ).toBeVisible();
    }
  });

  test('a deep link is rewritten to the app, not to an S3 error', async ({ page }) => {
    // The distribution rewrites 403/404 to index.html for SPA routes. Without
    // it, `/cases` pasted into a browser answers an XML AccessDenied page.
    const response = await page.goto('/cases');
    expect(response?.status(), 'a deep link should answer 200 through the rewrite').toBe(200);
    await expect(
      signInButton(page),
      'a deep link should land on the app (signed out here) rather than an error body',
    ).toBeVisible();
  });

  test('"Sign in" hands off to this environment’s own pool', async ({ page }) => {
    await page.goto('/');
    const appOrigin = new URL(baseUrl()).origin;

    await signInButton(page).click();
    await page.waitForURL((url) => isCognitoHost(url.hostname));
    const landed = new URL(page.url());

    expect(
      isCognitoHost(landed.hostname),
      `"Sign in" should redirect to the Cognito hosted UI (expected ${cognitoHostDescription()}, ` +
        `landed on ${landed.hostname})`,
    ).toBe(true);

    // The redirect names the client and the callback in its query string;
    // both are public values and both are exactly what the pool matches on.
    const clientId = cognitoClientId();
    if (clientId !== undefined) {
      expect(
        landed.searchParams.get('client_id'),
        'the authorize request should name this environment’s web app client — ' +
          'another id means the bundle inlined a stale EXPO_PUBLIC_COGNITO_CLIENT_ID',
      ).toBe(clientId);
    }
    expect(
      landed.searchParams.get('redirect_uri'),
      'the callback should be this origin’s /auth/callback — Cognito matches it exactly',
    ).toBe(`${appOrigin}/auth/callback`);
    expect(
      landed.searchParams.get('code_challenge'),
      'the authorize request should carry a PKCE challenge (ADR 0007)',
    ).toBeTruthy();

    // The form rendering is the pool's own verdict on the client and callback:
    // an unregistered callback or a client with no managed-login style makes
    // Cognito serve an error page on this same host instead.
    await expect(
      page.locator('form#primary-form'),
      'the Cognito page should show its sign-in form — an error page here means ' +
        'the app client rejected the redirect (callback not registered, or the ' +
        'managed-login style is missing and Cognito serves "Login pages unavailable")',
    ).toBeVisible();
  });
});
