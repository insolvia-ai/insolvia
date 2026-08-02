import { expect, test, type Page } from '@playwright/test';

import { cognitoHostDescription, isCognitoHost, testUser } from '../support/env';

/**
 * The authenticated round trip against deployed staging (issue #80, seed of the
 * suite in #40).
 *
 * WHAT THIS PROVES that a `curl /` smoke check cannot: that the bundle shipped
 * with a usable `EXPO_PUBLIC_COGNITO_DOMAIN` / `EXPO_PUBLIC_COGNITO_CLIENT_ID`
 * pair, that Cognito's app client still lists this origin's `/auth/callback` as
 * a callback URL (Cognito matches those EXACTLY — see infra/modules/auth), that
 * the code-for-token exchange works, and that the API accepts the resulting ID
 * token. The email assertion is the load-bearing one: the address only reaches
 * the screen if the whole `/v1/me` + ID-token loop closed.
 *
 * SELECTOR CONTRACT with the app. Role-based, by accessible name — the same
 * discipline `app-pr.yml`'s axe audit already enforces on the app, so these
 * selectors break only if the app's accessibility does:
 *
 *   - sign-in trigger : button named "Sign in"
 *   - sign-out control: button named "Sign out"
 *   - signed-in state : the user's email address rendered as visible text
 *
 * The Cognito hosted UI is NOT ours and gets no such contract, so its two
 * fields are addressed by the attribute names AWS has kept stable across both
 * the classic hosted UI and the newer managed login: `input[name="username"]`
 * and `input[name="password"]`. The submit control differs between the two
 * (`<input type="Submit" name="signInSubmitButton">` classic, `<button
 * type="submit">` managed), so it is matched as either.
 *
 * **The classic hosted UI renders the whole sign-in form TWICE**, in two
 * Bootstrap 3 responsive containers — `visible-xs visible-sm` and
 * `visible-md visible-lg` — so every field exists twice, with duplicate
 * element ids, and exactly one copy is displayed at any viewport width.
 * Addressing a field without filtering for visibility picks whichever copy
 * comes first in the DOM (the small-viewport one), which at Playwright's
 * 1280px default is the hidden one; `fill()` then waits for it to become
 * visible and dies on the action timeout. Everything is therefore scoped to
 * `form[name="cognitoSignInForm"]:visible`. This is why the form is located
 * once, up front, with its own assertion: a bare timeout on `fill()` says
 * nothing, and the same symptom would appear if Cognito served an error page
 * (it serves those on this same host, so the host check above cannot catch it).
 *
 * NO SLEEPS. Every wait below is on a condition.
 */

const signInButton = (page: Page) => page.getByRole('button', { name: 'Sign in' });
const signOutButton = (page: Page) => page.getByRole('button', { name: 'Sign out' });

/** The one displayed copy of the hosted UI's sign-in form — see the note above. */
const hostedUiForm = (page: Page) => page.locator('form[name="cognitoSignInForm"]:visible');

test.describe('staging auth round trip', () => {
  test('signs in through the Cognito hosted UI and back out again', async ({ page }) => {
    const { email, password } = testUser();

    // Record every top-level navigation from here on. The return leg lands on
    // `/auth/callback?code=…` and the app then routes away from it as soon as
    // the exchange completes, so asserting on `page.url()` alone would be a
    // race against the app's own redirect. The recorded history is not.
    const visited: string[] = [];
    page.on('framenavigated', (frame) => {
      if (frame === page.mainFrame()) visited.push(frame.url());
    });

    // ── 1. The app, signed out ────────────────────────────────────────────
    await page.goto('/');
    const appOrigin = new URL(page.url()).origin;
    await expect(
      signInButton(page),
      'the staging app should render a "Sign in" button when signed out',
    ).toBeVisible();

    // ── 2. Off to the hosted UI ───────────────────────────────────────────
    await signInButton(page).click();
    await page.waitForURL((url) => isCognitoHost(url.hostname));

    // `waitForURL` above would have timed out had this not held; restating it
    // as an assertion is what puts the expected host in the failure output
    // instead of a bare timeout.
    expect(
      isCognitoHost(new URL(page.url()).hostname),
      `"Sign in" should redirect to the Cognito hosted UI (expected ${cognitoHostDescription()}, ` +
        `landed on ${new URL(page.url()).hostname})`,
    ).toBe(true);

    // ── 3. Authenticate ───────────────────────────────────────────────────
    //
    // The only two lines in this suite that touch the credentials. `fill()`
    // sets the value through the DOM — it is not typed into a log, not
    // interpolated into a URL, and no trace is recorded in CI (see
    // playwright.config.ts for why).
    const form = hostedUiForm(page);
    await expect(
      form,
      'the Cognito hosted UI should show its sign-in form — if this fails, the ' +
        'host redirected but served something else (an OAuth error page lives on ' +
        'this same host), or the responsive form containers changed',
    ).toBeVisible();

    await form.locator('input[name="username"]').fill(email);
    await form.locator('input[name="password"]').fill(password);
    await form
      .locator('input[name="signInSubmitButton"], input[type="Submit" i], button[type="submit"]')
      .first()
      .click();

    // ── 4. The return leg ─────────────────────────────────────────────────
    //
    // Wait for the app to have taken over: back on our own origin, and off the
    // callback route. `expect.poll` below retries its predicate on the
    // configured expect timeout — a condition, not a sleep.
    await page.waitForURL(
      (url) => url.origin === appOrigin && !url.pathname.startsWith('/auth/callback'),
      { timeout: 30_000 },
    );

    await expect
      .poll(() => visited.some((href) => new URL(href).pathname.startsWith('/auth/callback')), {
        message:
          'the hosted UI should redirect back to /auth/callback — if it did not, the ' +
          'app client\'s callback URLs no longer match this origin (they match exactly)',
      })
      .toBe(true);

    expect(
      new URL(page.url()).pathname,
      'the app should route away from /auth/callback once the code exchange completes',
    ).not.toContain('/auth/callback');

    // ── 5. The signed-in identity ─────────────────────────────────────────
    //
    // This is the assertion issue #80 exists for: the address is on screen only
    // if the token exchange succeeded, the ID token was accepted, and the app
    // rendered the identity it got back.
    await expect(
      page.getByText(email).first(),
      'the signed-in app should render the test user\'s email address — its absence ' +
        'means the /v1/me + ID-token loop did not close',
    ).toBeVisible();
    await expect(signOutButton(page)).toBeVisible();

    // ── 6. Sign out ───────────────────────────────────────────────────────
    await signOutButton(page).click();
    await expect(
      signInButton(page),
      'signing out should return the app to its signed-out state',
    ).toBeVisible();
    await expect(
      page.getByText(email),
      'the signed-out app should render no trace of the previous identity',
    ).toHaveCount(0);
  });
});
