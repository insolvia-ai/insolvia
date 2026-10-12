import { expect, test } from '@playwright/test';

import {
  cognitoHostDescription,
  cognitoPortalClientId,
  isCognitoHost,
  seededClient,
} from '../../support/env';

/**
 * The client portal, signed in as the seeded debtor (ADR 0023 PR 2's
 * done-when): the seeded client signs in through the PORTAL's app client and
 * sees their firm's name and the case's chapter and stage — nothing else.
 *
 * WHAT ONLY THIS CAN PROVE: that the deployed bundle carries a usable
 * `EXPO_PUBLIC_COGNITO_PORTAL_CLIENT_ID`; that the portal app client still
 * registers this origin's `/portal/auth/callback` (exact match); that the
 * managed login pages are branded for that client (a client with none serves
 * "Login pages unavailable"); that the seeded binding resolves; and that the
 * API's projection answers the case's chapter and stage through it.
 *
 * WHO comes from `seeds/<target>.json`'s `cases[].clients` (handle `client`),
 * the same rows the seeder binds — the firm name from that entry and the
 * chapter from the fixture case it names. The stage is one of the three the
 * portal can show; which one is the firm's to change, so it is not pinned.
 *
 * Cognito's page selectors are `auth-round-trip.spec.ts`'s contract; read its
 * header before changing them. The one `fill()` of the password is here.
 */

const STAGE_LABELS = /^(Preparing your case|Ready to file|Filed)$/;

test.describe('client portal', () => {
  test('the seeded client signs in and sees their firm and their case stage', async ({ page }) => {
    const client = seededClient('client');

    // ── 1. /portal, signed out, goes to the PORTAL sign-in ────────────────
    await page.goto('/portal');
    const appOrigin = new URL(page.url()).origin;
    await expect(page).toHaveURL(/\/portal\/sign-in/);
    await expect(
      page.getByRole('heading', { level: 1, name: 'Sign in to your client portal' }),
    ).toBeVisible();

    // ── 2. Managed login, through the portal's own app client ─────────────
    await page.getByRole('button', { name: 'Sign in' }).click();
    await page.waitForURL((url) => isCognitoHost(url.hostname), {
      timeout: 15_000,
    });
    const authorize = new URL(page.url());
    expect(isCognitoHost(authorize.hostname), `expected ${cognitoHostDescription()}`).toBe(true);
    const expectedClient = cognitoPortalClientId();
    if (expectedClient !== undefined) {
      // The query survives managed login's own redirect onto /login.
      expect(authorize.searchParams.get('client_id')).toBe(expectedClient);
    }

    const form = page.locator('form#primary-form');
    await expect(form, 'the Cognito page should show its sign-in form').toBeVisible();
    await form.locator('input[name="username"]').fill(client.email);
    await form.locator('input[name="password"]').fill(client.password);
    await form.getByRole('button', { name: 'Sign in' }).click();

    // ── 3. Back on the portal landing screen ──────────────────────────────
    await page.waitForURL((url) => url.origin === appOrigin && url.pathname === '/portal', {
      timeout: 30_000,
    });
    await expect(page.getByRole('heading', { level: 1, name: /^Welcome, / })).toBeVisible();

    const main = page.getByRole('main');
    await expect(main.getByText(client.firmName).first()).toBeVisible();
    await expect(
      main.getByText(`Chapter ${String(client.chapter)}`, { exact: true }),
    ).toBeVisible();
    await expect(main.getByText(STAGE_LABELS)).toBeVisible();

    // ── 4. Nothing else: the portal frame, not the firm's ─────────────────
    await expect(page.getByRole('link', { name: 'Cases' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Account menu' })).toHaveCount(0);

    // ── 5. Out again, through Cognito's /logout, back to the portal ───────
    await page.getByRole('button', { name: 'Sign out' }).click();
    await page.waitForURL((url) => url.origin === appOrigin && url.pathname.startsWith('/portal'), {
      timeout: 30_000,
    });
    await expect(
      page.getByRole('heading', { level: 1, name: 'Sign in to your client portal' }),
    ).toBeVisible();
  });
});
