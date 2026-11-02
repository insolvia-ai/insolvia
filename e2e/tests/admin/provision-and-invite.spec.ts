import { expect, test } from '@playwright/test';

import {
  STAFF_EMAIL,
  STAND_IN_BUTTON,
  useOfflineGoogleIdentity,
} from '../../support/google-identity-stand-in';

/**
 * Staff provision a firm and re-invite its first administrator, through the
 * admin portal, against the offline stack.
 *
 * WHAT THIS PROVES THAT NOTHING ELSE CAN. The contract suite already calls
 * every admin-client method against the same hosts; the portal's jest suite
 * renders its screens against a mocked client. Neither proves the built
 * portal's form posts what the service accepts, or that the session the
 * portal keeps from a Google ID token carries a request through. This walks
 * that whole path in a browser, with only Google's account chooser stood in.
 *
 * OFFLINE ONLY. It runs over `contract/scripts/dev-test.sh`'s throwaway
 * stack (a fresh Postgres per run), so it writes freely and cleans nothing:
 * the database is gone when the run ends. It never runs against a deployed
 * admin service — the stand-in's token is signed by a key only that stack
 * trusts.
 *
 * SELECTOR CONTRACT with `apps/insolvia_admin` (`src/screens/`):
 *
 *   - sign-in   : the button Google's script renders (here, the stand-in's)
 *   - firms     : level-1 heading "Firms"; link "Provision a firm"; the
 *                 header's link "Firms"; each firm's name as a link
 *   - provision : textboxes "Firm name", "First administrator's first name",
 *                 "First administrator's last name", "First administrator's
 *                 work email"; button "Provision firm"
 *   - the firm  : level-1 heading <firm name>; level-2 heading "People"; the
 *                 person's row, holding their email and button "Resend
 *                 invite"; the notice "A fresh invitation is on its way to
 *                 <email>."
 */

test('staff sign in, provision a firm and re-invite its administrator', async ({ page }) => {
  await useOfflineGoogleIdentity(page, process.env.CONTRACT_ADMIN_URL ?? '');
  const run = Date.now().toString(36);
  const firm = `E2E Probe Firm ${run}`;
  const email = `e2e-admin-${run}@firm.test`;

  // ── Sign in ─────────────────────────────────────────────────────────
  await page.goto('/');
  await page.getByRole('button', { name: STAND_IN_BUTTON }).click();
  await expect(
    page.getByRole('heading', { level: 1, name: 'Firms' }),
    `signing in as ${STAFF_EMAIL} should land on the firm list — a sign-in screen here means ` +
      'the admin service refused the token (its GOOGLE_JWKS_URL or client id)',
  ).toBeVisible();

  // ── Provision ───────────────────────────────────────────────────────
  await page.getByRole('link', { name: 'Provision a firm' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Provision a firm' })).toBeVisible();
  await page.getByRole('textbox', { name: 'Firm name' }).fill(firm);
  await page.getByRole('textbox', { name: "First administrator's first name" }).fill('Probe');
  await page.getByRole('textbox', { name: "First administrator's last name" }).fill('Admin');
  await page.getByRole('textbox', { name: "First administrator's work email" }).fill(email);
  await page.getByRole('button', { name: 'Provision firm' }).click();

  await expect(
    page.getByRole('heading', { level: 1, name: firm }),
    'provisioning should open the new firm — an alert on the form says what the service refused',
  ).toBeVisible();
  await expect(page.getByRole('heading', { level: 2, name: 'People' })).toBeVisible();
  const person = page.getByRole('row').filter({ hasText: email });
  await expect(person, 'the first administrator should be listed under People').toHaveCount(1);
  await expect(person).toContainText('administrator');

  // ── Invite again ────────────────────────────────────────────────────
  await person.getByRole('button', { name: 'Resend invite' }).click();
  await expect(page.getByText(`A fresh invitation is on its way to ${email}.`)).toBeVisible();

  // ── The firm list knows it ──────────────────────────────────────────
  // Through the header link, not a reload: the staff session is memory-only
  // by design (src/session/session.tsx), so a reload signs out.
  await page.getByRole('link', { name: 'Firms', exact: true }).click();
  await expect(page.getByRole('link', { name: firm })).toBeVisible();
});
