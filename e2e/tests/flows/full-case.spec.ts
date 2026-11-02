import { expect, type Page, test } from '@playwright/test';

import { followCapability } from '../../support/capability';
import { ensureScratchResidence, SCRATCH_RESIDENCE } from '../../support/scratch-residence';
import { signIn } from '../../support/sign-in';

/**
 * One case, front to back: intake → debtor → documents → forms hub →
 * filing-set preview, on the suite's scratch case.
 *
 * WHAT THIS PROVES THAT NOTHING ELSE CAN. Each screen has its own jest suite
 * and each route its own contract test; what none of them holds is that the
 * SAME case reads the same everywhere a preparer walks. The debtor copied from
 * the client record has to be the one the overview's readiness counts, the
 * forms hub has to list the forms THIS chapter requires and render one from
 * the stored case through the real PDF engine into the real bucket, and the
 * filing set has to order those forms for the court the case was opened in —
 * and hold the case back while the petition is incomplete, naming why, with
 * a link that lands on the screen that fixes it. Those are joins across five
 * screens, three API route families, the forms engine and S3, and a browser
 * driving one case is the only place they meet.
 *
 * WHAT IT DELIBERATELY DOES NOT DO. It never presses "Assemble packet".
 * Assembly is a queued job (ADR 0018) that a worker runs: each press would
 * leave a job record and, on a complete case, a packet and its objects that
 * nothing deletes; and the compose stack runs no worker, so locally the job
 * would never settle. The checklist above it is computed from the same stored
 * case without a job, and is the gate's assertion instead. Document upload and the means
 * test have specs of their own (`documents.spec.ts`, `means-test.spec.ts`);
 * this one only reads those screens.
 *
 * IT WRITES ONLY TO THE SUITE'S SCRATCH PAIR — the client's residence and the
 * case's Debtor 1, both through `support/scratch-residence.ts`, both the same
 * values on every run. The PDF preview it renders is the API's to keep or
 * expire; nothing here is left behind per run.
 *
 * NO PRESIGNED URL IS LOGGED. The preview's URL is a bearer capability; it is
 * fetched and its bytes checked, never interpolated into a message.
 *
 * SELECTOR CONTRACT with `apps/insolvia_app` (plus `support/scratch-case.ts`
 * and `support/scratch-residence.ts`):
 *
 *   - section strip  : navigation "Case sections"; links "Overview", "Intake",
 *                      "Documents", "Forms", "Filing packet"
 *   - overview       : heading "Filing readiness"; the stage text "Debtor intake"
 *   - intake         : tab "Debtor 1"; textboxes "Street", "City", "ZIP code"
 *                      (the first of each is where the debtor lives)
 *   - documents      : heading "Case documents"; heading "Documents in this case"
 *   - forms hub      : heading "Forms"; each form a listitem headed
 *                      "<number> — <title>"; button "Preview <title> as a PDF"
 *   - filing packet  : heading "Filing packet"; heading "Filing set and
 *                      checklist"; listitems "1. 01-b101.pdf …" (the first such —
 *                      "Approve this filing" repeats the list); link "Go to
 *                      fix: Case data: petitions"
 *   - petition       : heading "Petition (B101)"
 */

async function openSection(page: Page, name: string): Promise<void> {
  await page.getByRole('navigation', { name: 'Case sections' }).getByRole('link', { name }).click();
}

test.describe('one full case', () => {
  test('intake to the filing set, on one case', async ({ page }) => {
    // The page drives five screens and renders one PDF; a cold Lambda for the
    // forms engine on staging is the slow part.
    test.setTimeout(180_000);
    await signIn(page);

    // ── 1. Intake → debtor ────────────────────────────────────────────────
    //
    // The address goes on the CLIENT record and is re-copied onto the case's
    // Debtor 1, as a preparer does it (ADR 0022). The helper lands on the
    // intake with the copy made; the debtor tab must now show the address.
    await ensureScratchResidence(page);
    await expect(page.getByRole('tab', { name: 'Debtor 1' })).toHaveAttribute(
      'aria-selected',
      'true',
    );
    await expect(page.getByRole('textbox', { name: 'Street', exact: true }).first()).toHaveValue(
      SCRATCH_RESIDENCE.street,
    );
    await expect(page.getByRole('textbox', { name: 'City', exact: true }).first()).toHaveValue(
      SCRATCH_RESIDENCE.city,
    );
    await expect(page.getByRole('textbox', { name: 'ZIP code', exact: true }).first()).toHaveValue(
      SCRATCH_RESIDENCE.zip,
    );

    // ── 2. The overview counts that debtor ────────────────────────────────
    await openSection(page, 'Overview');
    await expect(page.getByRole('heading', { level: 2, name: 'Filing readiness' })).toBeVisible();
    await expect(
      page.getByText('1 debtor recorded'),
      "the overview's readiness should count the debtor the intake just showed",
    ).toBeVisible({ timeout: 15_000 });

    // ── 3. Documents — read only; documents.spec.ts owns the upload ───────
    await openSection(page, 'Documents');
    await expect(page.getByRole('heading', { level: 1, name: 'Case documents' })).toBeVisible();
    await expect(
      page.getByRole('heading', { level: 2, name: 'Documents in this case' }),
    ).toBeVisible();
    await expect(page.getByText('Loading…')).toHaveCount(0, { timeout: 15_000 });

    // ── 4. Forms hub: the chapter's forms, and one rendered for real ──────
    await openSection(page, 'Forms');
    await expect(page.getByRole('heading', { level: 1, name: 'Forms' })).toBeVisible();
    const petition = page
      .getByRole('listitem')
      .filter({ hasText: /^101 — Voluntary Petition for Individuals/ });
    await expect(
      petition,
      'the forms hub should list the Chapter 7 set, the petition first',
    ).toBeVisible({ timeout: 30_000 });
    await expect(
      page.getByRole('listitem').filter({ hasText: /^122A-1 — Chapter 7 Statement/ }),
      'a Chapter 7 case should be asked for Form 122A-1',
    ).toBeVisible();
    // The petition is incomplete on purpose (see the header), and says so.
    await expect(petition).toContainText(/\d+ issues?/);

    // B121 needs nothing the scratch case lacks, so its preview renders. The
    // app opens the presigned URL in a new tab; `followCapability` reads it
    // again and the test checks it is a PDF.
    const ssnForm = 'Statement About Your Social Security Numbers';
    const preview = await followCapability(page, () =>
      page.getByRole('button', { name: `Preview ${ssnForm} as a PDF` }).click(),
    );
    expect(preview.status, 'the rendered preview should be readable from its presigned URL').toBe(
      200,
    );
    expect(
      preview.body.subarray(0, 5).toString('latin1'),
      'the preview should be a PDF, rendered by the forms engine from the stored case',
    ).toBe('%PDF-');
    await expect(page.getByText(`Opened ${ssnForm}.`)).toBeVisible();

    // ── 5. The filing set: ordered for this court, gated on the petition ──
    await openSection(page, 'Filing packet');
    await expect(page.getByRole('heading', { level: 1, name: 'Filing packet' })).toBeVisible();
    await expect(
      page.getByRole('heading', { level: 2, name: 'Filing set and checklist' }),
    ).toBeVisible();
    await expect(page.getByText('Loading the filing set…')).toHaveCount(0, { timeout: 30_000 });
    // The same docket list appears again under "Approve this filing", below;
    // the filing set's is the first in the page.
    await expect(
      page.getByRole('listitem').filter({ hasText: /^1\. 01-b101\.pdf/ }).first(),
      'the filing set should put the petition first, under its docket file name',
    ).toBeVisible();

    // The checklist is the gate, computed from the stored case: the petition's
    // own answers are missing, and the item links to the screen that fixes it.
    const fix = page.getByRole('link', { name: 'Go to fix: Case data: petitions' });
    await expect(
      fix,
      "an incomplete petition should block the filing set's checklist",
    ).toBeVisible();
    await fix.click();
    await expect(
      page.getByRole('heading', { level: 1, name: 'Petition (B101)' }),
      "the petition's checklist item should link to the petition screen",
    ).toBeVisible();
  });
});
