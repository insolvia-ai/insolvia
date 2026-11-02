import { expect, type Page } from '@playwright/test';

import { openScratchCase, scratchClientLinks, searchClientList } from './scratch-case';

/**
 * Gives the scratch client a home and copies it onto the scratch case's
 * Debtor 1 — the staff path a real address takes, and the one the means test
 * depends on.
 *
 * WHY THROUGH THE CLIENT RECORD. The intake's debtor tab edits street, city,
 * state and ZIP, but not the COUNTY: that lives on the client record's
 * residence address and reaches a case only by "Re-copy from client" (ADR
 * 0022: the client is the source, a case holds a copy). The means test needs
 * both — the median table is keyed by state, and the 122A-2 housing and
 * vehicle allowances by county — so a spec that wants an above-median verdict
 * has to walk this path, exactly as a preparer would.
 *
 * Idempotent on the reused pair: the same address is saved every time, and
 * the re-copy runs only when the intake reports the case differs from the
 * record (which `intake-persists.spec.ts`'s probe first name makes it do on
 * most runs — the re-copy puts "Scratch" back, which nothing relies on).
 *
 * The address is made up and belongs to nobody: a street name chosen to read
 * as test data, in a real county because the IRS tables only know real ones.
 *
 * SELECTOR CONTRACT with `apps/insolvia_app`:
 *
 *   - client record : button "Edit details"; textboxes "Street address",
 *     "City", "State", "ZIP code", "County" (the residence's — the mailing
 *     address's are prefixed "Mailing"); button "Save changes"; the edit form
 *     closes, and "Edit details" returns, on success
 *   - the case      : navigation "Case sections", link "Intake"; the debtor
 *     tab's first textbox "State"; button "Re-copy from client" while the
 *     case differs; the confirm dialog's button "Re-copy"
 */
export const SCRATCH_RESIDENCE = {
  street: '1 Probe Test Way',
  city: 'Miami',
  state: 'FL',
  zip: '33101',
  county: 'Miami-Dade',
} as const;

export async function ensureScratchResidence(page: Page): Promise<void> {
  // Opening the case first guarantees the pair exists on a fresh environment.
  await openScratchCase(page);

  // ── The client record ──────────────────────────────────────────────────
  await searchClientList(page);
  await scratchClientLinks(page).first().click();
  await page.getByRole('button', { name: 'Edit details' }).click();
  const field = (name: string) => page.getByRole('textbox', { name, exact: true });
  await field('Street address').fill(SCRATCH_RESIDENCE.street);
  await field('City').fill(SCRATCH_RESIDENCE.city);
  await field('State').fill(SCRATCH_RESIDENCE.state);
  await field('ZIP code').fill(SCRATCH_RESIDENCE.zip);
  await field('County').fill(SCRATCH_RESIDENCE.county);
  await page.getByRole('button', { name: 'Save changes' }).click();
  await expect(
    page.getByRole('button', { name: 'Edit details' }),
    'saving the client record should close the edit form — if it is still open, ' +
      'the API refused the address and the status line says why',
  ).toBeVisible({ timeout: 15_000 });

  // ── Onto the case's debtor ─────────────────────────────────────────────
  await openScratchCase(page);
  await page
    .getByRole('navigation', { name: 'Case sections' })
    .getByRole('link', { name: 'Intake' })
    .click();
  const state = page.getByRole('textbox', { name: 'State', exact: true }).first();
  await expect(state).toBeVisible();
  // The client panel loads after the debtor; its heading names the client,
  // and the "differs" banner (with the re-copy button) renders with it.
  await expect(
    page.getByRole('heading', { level: 3, name: /^Client: / }),
    "the intake should show which client Debtor 1 was copied from",
  ).toBeVisible();
  const recopy = page.getByRole('button', { name: 'Re-copy from client' });
  if ((await recopy.count()) > 0) {
    await recopy.click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Re-copy' }).click();
  }
  await expect(
    state,
    "the case's Debtor 1 should carry the client's residence after the re-copy",
  ).toHaveValue(SCRATCH_RESIDENCE.state, { timeout: 15_000 });
}
