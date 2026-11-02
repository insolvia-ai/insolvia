import { expect, type Page, test } from '@playwright/test';

import { ensureScratchResidence } from '../../support/scratch-residence';
import { signIn } from '../../support/sign-in';

/**
 * The means test answers from what the preparer typed (§ 707(b), Forms
 * 122A-1 / 122A-2), and keeps the answer.
 *
 * WHAT THIS PROVES THAT NOTHING ELSE CAN. The verdict on this screen is the
 * end of a chain no single lower suite holds:
 *
 *   - The engine's xUnit suite proves the arithmetic, from inputs it builds
 *     itself. It cannot tell whether the screen sends those inputs.
 *   - The app's jest suite renders the banner from a canned trace. It cannot
 *     tell whether the trace it would really get says the same thing.
 *   - The contract suite proves the routes through the shipped client. It
 *     does not type into the override grid, and it does not know which of
 *     the grid's fifty boxes is line 2.
 *
 * Moving one income figure across the median and watching the verdict follow
 * — then finding it still there after a reload — is the one test that the
 * screen's save, the API's record, the engine and the regulatory datasets the
 * environment was deployed with all agree.
 *
 * DETERMINISTIC ON A REUSED CASE. Every run writes the same three answers in
 * the same order and ends on the same one, so a second run starts where the
 * first finished and asserts nothing that depended on the starting state.
 * The two incomes are a long way either side of any state's single-person
 * median (roughly $45k-$80k a year), so a dataset refresh cannot flip either
 * verdict.
 *
 * IT WRITES ONLY TO THE SUITE'S SCRATCH CASE (`support/scratch-case.ts`).
 *
 * SELECTOR CONTRACT with `apps/insolvia_app` (`src/screens/means-test/`):
 *
 *   - the case's section strip : navigation "Case sections", link "Means test"
 *   - the screen               : level-1 heading "Means test"
 *   - the household            : textboxes "People under 65", "People 65 or older"
 *   - line 2's override        : textbox "Column A — Debtor 1: Line 2 — Gross
 *                                wages, salary, tips, bonuses, overtime, commissions"
 *   - saving                   : button "Save and recompute" (there are two;
 *                                either saves the whole record); the status
 *                                text "Saved"
 *   - the verdict              : the texts "At or below the median" / "Above
 *                                the median", "No presumption of abuse" /
 *                                "Presumption of abuse arises"
 */

const LINE_2 =
  'Column A — Debtor 1: Line 2 — Gross wages, salary, tips, bonuses, overtime, commissions';

/** A monthly figure far below any single-person median, and one far above. */
const BELOW = '1500';
const ABOVE = '25000';

async function saveAndRecompute(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Save and recompute' }).first().click();
  // "Saved — recomputing" while the trace reloads, then exactly "Saved": the
  // verdict below is the recomputed one only after the second.
  await expect(
    page.getByText('Saved', { exact: true }),
    'the means test should save and recompute — "Some answers need attention" here means ' +
      'the API rejected the record, most likely its provenance map',
  ).toBeVisible({ timeout: 30_000 });
}

test.describe('the means test', () => {
  test('follows an income across the median and keeps the answer', async ({ page }) => {
    await signIn(page);

    // ── 0. Where the debtor lives — the median table is keyed by the state,
    //      the 122A-2 allowances by the county. Both come from the client
    //      record, through the intake's re-copy, never from this screen.
    await ensureScratchResidence(page);

    await page
      .getByRole('navigation', { name: 'Case sections' })
      .getByRole('link', { name: 'Means test' })
      .click();
    await expect(page.getByRole('heading', { level: 1, name: 'Means test' })).toBeVisible();
    await expect(page.getByText('Computing the means test…')).toHaveCount(0, {
      timeout: 30_000,
    });

    // ── 1. The household, which the median comparison cannot run without ──
    await page.getByRole('textbox', { name: 'People under 65', exact: true }).fill('1');
    await page.getByRole('textbox', { name: 'People 65 or older', exact: true }).fill('0');

    // ── 2. Below the median: no presumption ─────────────────────────────
    await page.getByRole('textbox', { name: LINE_2 }).fill(BELOW);
    await saveAndRecompute(page);
    await expect(
      page.getByText('At or below the median', { exact: true }),
      'a $1,500 month for one person should compare below every state median — ' +
        '"Comparison not yet made" means the household did not reach the engine',
    ).toBeVisible();
    await expect(page.getByText('No presumption of abuse', { exact: true })).toBeVisible();

    // ── 3. Above it: the 122A-2 calculation runs and the presumption arises ─
    //
    // With no deductions entered, $25,000 a month leaves far more than the
    // § 707(b)(2) threshold, so the verdict is not a judgement call.
    await page.getByRole('textbox', { name: LINE_2 }).fill(ABOVE);
    await saveAndRecompute(page);
    await expect(
      page.getByText('Above the median', { exact: true }),
      'moving line 2 above the median should flip the comparison — if it did not, ' +
        'the override the screen sent is not the one the engine read',
    ).toBeVisible();
    await expect(page.getByText('Presumption of abuse arises', { exact: true })).toBeVisible();

    // ── 4. The record, not the screen's memory ───────────────────────────
    await page.reload();
    await expect(page.getByRole('textbox', { name: LINE_2 })).toHaveValue(/^25,?000(\.00)?$/, {
      timeout: 30_000,
    });
    await expect(
      page.getByText('Above the median', { exact: true }),
      'after a reload the verdict should be recomputed from the stored inputs',
    ).toBeVisible({ timeout: 30_000 });
  });
});
