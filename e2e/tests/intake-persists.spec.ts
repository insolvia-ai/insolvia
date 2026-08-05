import { expect, test } from '@playwright/test';

import { signIn } from '../support/sign-in';

/**
 * The intake keeps what you typed (issue 8.5), against deployed staging.
 *
 * WHAT THIS PROVES THAT NOTHING ELSE CAN. The intake's one promise is that a
 * half-finished questionnaire survives — and every layer between the keystroke
 * and the row has a way to break that which its own tests cannot see:
 *
 *   - The app's jest suite asserts the request body against a fetch mock. It
 *     cannot tell whether the API accepts that body.
 *   - The API's pytest suite runs against an in-memory store. It cannot tell
 *     whether DynamoDB accepts the item.
 *   - The store's own probe runs against a dev table with no auth in front of
 *     it. It cannot tell whether a signed-in user reaches their own case.
 *
 * Typing a name here and finding it after a reload closes all three at once,
 * and it closes the one that has actually bitten: the API rejects any body
 * where a populated field carries no provenance, so a client that derives that
 * map wrongly fails EVERY save. That failure is invisible to a mock.
 *
 * SELECTOR CONTRACT with the app — role-based, by accessible name, the same
 * discipline the rest of the suite keeps:
 *
 *   - the case list's link : link named "Open intake for the chapter N case in D"
 *   - the intake's name box: textbox named "First name"
 *   - the autosave signal  : the text "Saved"
 *
 * IT REUSES A CASE rather than opening one per run. A spec that creates a row
 * on every staging deploy is a spec that fills a table nobody prunes; opening
 * one only when the account has none keeps this to a single case forever.
 *
 * NO SLEEPS. The autosave is debounced, so the wait is on the "Saved" signal
 * the screen renders — a condition, not a guess at the debounce interval.
 */

// Distinct per run so a stale value from a previous deploy cannot make this
// pass. Not a real name, and nothing about a person: this is a public repo and
// staging is synthetic.
const typedName = `Probe${Date.now().toString(36)}`;

test.describe('staging intake', () => {
  test('keeps a half-finished intake across a reload', async ({ page }) => {
    await signIn(page);

    // ── 1. A case to work in ──────────────────────────────────────────────
    await page.goto('/cases');
    const intakeLink = page.getByRole('link', { name: /^Open intake for the chapter/ }).first();

    if ((await intakeLink.count()) === 0) {
      // First run against a fresh environment: open one. The chapter radio
      // defaults to 7, so only the district needs an answer.
      await page.getByRole('textbox', { name: 'Filing district' }).fill('NDCA');
      await page.getByRole('button', { name: 'Open case' }).click();
    }
    await expect(
      intakeLink,
      "the case list should offer a way into a case's intake — its absence means " +
        'either the list did not load or the link lost its accessible name',
    ).toBeVisible();

    // ── 2. Type into the intake ───────────────────────────────────────────
    await intakeLink.click();
    const firstName = page.getByRole('textbox', { name: 'First name' }).first();
    await expect(
      firstName,
      'the intake should render the debtor form — a 404 here means the dynamic ' +
        'route did not survive the single-output web export',
    ).toBeVisible();

    await firstName.fill(typedName);

    // ── 3. The save is the assertion ──────────────────────────────────────
    //
    // "Saved" appears only after the API returned 2xx. A body whose provenance
    // map did not cover every populated field would come back 400 and this
    // would read "Some answers need attention" instead — which is exactly the
    // failure no mock can produce.
    await expect(
      page.getByText('Saved'),
      'the intake should report a completed save — "Some answers need attention" ' +
        'here means the API rejected the body, most likely its provenance map',
    ).toBeVisible({ timeout: 15_000 });

    // ── 4. It is still there after a reload ───────────────────────────────
    //
    // The whole point. This reads the record back out of DynamoDB through a
    // fresh GET, so it proves the write landed rather than that the screen
    // remembered its own state.
    await page.reload();
    await expect(
      page.getByRole('textbox', { name: 'First name' }).first(),
      'the intake should still hold what was typed after a reload — if it is ' +
        'empty, the save did not reach the store or the read did not find it',
    ).toHaveValue(typedName);
  });
});
