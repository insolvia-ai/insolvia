import { expect, test } from '@playwright/test';

import {
  openScratchCase,
  SCRATCH_CLIENT,
  scratchCaseLinks,
  scratchClientLinks,
  searchClientList,
} from '../../support/scratch-case';
import { signIn } from '../../support/sign-in';

/**
 * The client list is the front door (ADR 0022 / #354): a case is found by
 * whose it is.
 *
 * WHAT THIS PROVES THAT NOTHING ELSE CAN. The app's jest suite renders the
 * list against a fetch mock; the API's suites prove the `by-client` index and
 * the per-case filter against a memory store and, in the integration tier,
 * the real table. Only a browser against a deployed stack proves the whole
 * chain a preparer walks: the directory loads, the search narrows it, the
 * row's case links come from `GET /v1/firm/clients/<id>/cases` through the
 * real index, and following one lands on that case.
 *
 * IT WRITES NOTHING BUT THE SUITE'S OWN SCRATCH PAIR, through
 * `support/scratch-case.ts` — which finds the scratch client and case (or,
 * on an environment's first run, adds them) and never touches a seeded
 * fixture client or case. Everything after that call is read-only.
 *
 * SELECTOR CONTRACT: `support/scratch-case.ts`'s header, plus the case
 * heading naming its debtors.
 */
test.describe('the client list', () => {
  test("finds the scratch client and opens its case from the client's row", async ({ page }) => {
    await signIn(page);

    // Ensures the pair exists — found, or opened on a fresh environment.
    await openScratchCase(page);

    // ── The list finds the client by name ─────────────────────────────────
    await searchClientList(page);
    const row = scratchClientLinks(page).first();
    await expect(
      row,
      'searching the client list for the scratch surname should find its row',
    ).toBeVisible();

    // ── The row carries the client's case, from the by-client index ──────
    const caseLink = scratchCaseLinks(page).first();
    await expect(
      caseLink,
      "the scratch client's row should link its case — its absence means " +
        'GET /v1/firm/clients/<id>/cases did not return it (the by-client index)',
    ).toBeVisible({ timeout: 15_000 });

    // ── Following it lands on the case, whose heading names the client ───
    await caseLink.click();
    await expect(
      page.getByRole('heading', { level: 1 }).first(),
      "the case opened from the client's row should be that client's case",
    ).toContainText(SCRATCH_CLIENT.surname);
  });
});
