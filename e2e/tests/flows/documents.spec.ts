import { expect, type Locator, type Page, test } from '@playwright/test';

import { followCapability } from '../../support/capability';
import { openScratchCase } from '../../support/scratch-case';
import { signIn } from '../../support/sign-in';

/**
 * A document asked for, and a document uploaded, on the scratch case.
 *
 * WHAT THESE PROVE THAT NOTHING ELSE CAN.
 *
 *   - THE UPLOAD is three hops across two origins: the API mints a document
 *     row and a presigned PUT, the BROWSER sends the bytes straight to S3,
 *     and the API confirms against what S3 counted. The app's jest suite
 *     mocks `fetch`; the contract suite uploads from Node, where there is no
 *     CORS and no file picker. Only a browser proves the bucket's CORS admits
 *     the app's origin, that the picker hands over a `File` the client can
 *     sign for, and that the presigned GET hands the same bytes back.
 *   - THE REQUEST is what a preparer asks the client for (ADR 0023 PR 5).
 *     The routes are the contract suite's; what only a browser shows is the
 *     overview panel doing the whole round — ask, mark not needed, withdraw —
 *     and each state surviving a reload, i.e. coming back from the store.
 *
 * NOTHING ACCUMULATES. Neither a document nor a request is left behind: each
 * test removes what it added, and FIRST sweeps anything an earlier, failed
 * run left (by the probe prefix, which no person would type). So a second run
 * against the same environment starts from the same empty state — the run
 * twice in a row is what proves it. A deleted document is gone from the case
 * at once; the API keeps it recoverable internally for 30 days and then it is
 * purged, so even the deletes do not grow anything.
 *
 * IT WRITES ONLY TO THE SUITE'S SCRATCH CASE (`support/scratch-case.ts`).
 * The seeded fixture case's two documents are read by the contract suite's
 * deployed target and are never touched here.
 *
 * NO PRESIGNED URL IS LOGGED. A presigned GET is a bearer capability; it is
 * compared byte-for-byte below and never interpolated into a message.
 *
 * SELECTOR CONTRACT with `apps/insolvia_app`:
 *
 *   - section strip : navigation "Case sections", links "Overview", "Documents"
 *   - requests panel: heading "Requested documents"; button "Request another
 *     document"; textbox "Document"; combobox "Filed as"; button "Request
 *     it"; each request a listitem holding its title and a status badge
 *     ("Outstanding" / "Not needed" / "Arrived"); buttons "Mark <title> not
 *     needed", "Withdraw <title>"
 *   - documents     : heading "Case documents"; combobox "Document type";
 *     button "Choose a file to upload to this case" (opens the file picker);
 *     button "Upload <file>"; each document a listitem; buttons "Download
 *     <file>", "Delete <file>"; the alertdialog's button "Delete document"
 */

const PROBE = 'e2e-probe';
const run = Date.now().toString(36);

/** A one-page PDF, small enough to read in a diff, valid enough for the API's sniff. */
const PDF = Buffer.from(
  '%PDF-1.4\n' +
    '1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n' +
    '2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n' +
    '3 0 obj<</Type/Page/MediaBox[0 0 612 792]/Parent 2 0 R>>endobj\n' +
    'trailer<</Root 1 0 R>>\n%%EOF\n',
);

async function openSection(page: Page, name: string): Promise<void> {
  await page.getByRole('navigation', { name: 'Case sections' }).getByRole('link', { name }).click();
}

const row = (page: Page, text: string): Locator =>
  page.getByRole('listitem').filter({ hasText: text });

test.describe('documents', () => {
  test('a requested document is asked for, set aside and withdrawn', async ({ page }) => {
    const title = `${PROBE} request ${run}`;
    await signIn(page);
    await openScratchCase(page);

    const panel = page.getByRole('heading', { level: 2, name: 'Requested documents' });
    await expect(panel).toBeVisible();
    await expect(page.getByText('Loading requested documents…')).toHaveCount(0);

    // ── Sweep: withdraw what an earlier, failed run left ──────────────────
    const leftovers = page.getByRole('button', { name: new RegExp(`^Withdraw ${PROBE} request `) });
    while ((await leftovers.count()) > 0) {
      const before = await leftovers.count();
      await leftovers.first().click();
      await expect(leftovers).toHaveCount(before - 1, { timeout: 15_000 });
    }

    // ── Ask for it ────────────────────────────────────────────────────────
    await page.getByRole('button', { name: 'Request another document' }).click();
    await page.getByRole('textbox', { name: 'Document', exact: true }).fill(title);
    await page.getByRole('combobox', { name: 'Filed as' }).click();
    await page.getByRole('option', { name: 'Bank statement', exact: true }).click();
    await page.getByRole('button', { name: 'Request it' }).click();

    await expect(
      row(page, title),
      'the new request should be listed — "Some answers need attention" means the API ' +
        'refused the body',
    ).toBeVisible({ timeout: 15_000 });
    await expect(row(page, title)).toContainText('Outstanding');
    await expect(row(page, title)).toContainText('Bank statement');

    // ── Set it aside, and find it set aside after a reload ────────────────
    await page.getByRole('button', { name: `Mark ${title} not needed` }).click();
    await expect(row(page, title)).toContainText('Not needed', { timeout: 15_000 });
    await page.reload();
    await expect(
      row(page, title),
      'a waived request should come back waived — the status lives in the store, not the screen',
    ).toContainText('Not needed', { timeout: 15_000 });

    // ── Withdraw it: gone, and still gone after a reload ──────────────────
    await page.getByRole('button', { name: `Withdraw ${title}` }).click();
    await expect(row(page, title)).toHaveCount(0, { timeout: 15_000 });
    await page.reload();
    await expect(page.getByRole('heading', { level: 2, name: 'Requested documents' })).toBeVisible();
    await expect(page.getByText('Loading requested documents…')).toHaveCount(0);
    await expect(row(page, title)).toHaveCount(0);
  });

  test('a document uploads to the bucket, downloads intact and is deleted', async ({ page }) => {
    const fileName = `${PROBE}-${run}.pdf`;
    await signIn(page);
    await openScratchCase(page);
    await openSection(page, 'Documents');
    await expect(page.getByRole('heading', { level: 1, name: 'Case documents' })).toBeVisible();
    await expect(page.getByText('Loading…')).toHaveCount(0, { timeout: 15_000 });

    // ── Sweep: delete what an earlier, failed run left — finished or not ──
    const leftovers = page.getByRole('button', { name: new RegExp(`^Delete ${PROBE}.*\\.pdf$`) });
    while ((await leftovers.count()) > 0) {
      const before = await leftovers.count();
      await leftovers.first().click();
      await page.getByRole('alertdialog').getByRole('button', { name: 'Delete document' }).click();
      await expect(leftovers).toHaveCount(before - 1, { timeout: 15_000 });
    }

    // ── Choose and upload ─────────────────────────────────────────────────
    await page.getByRole('combobox', { name: 'Document type' }).click();
    await page.getByRole('option', { name: 'Bank statement', exact: true }).click();
    const chooser = page.waitForEvent('filechooser');
    await page.getByRole('button', { name: 'Choose a file to upload to this case' }).click();
    await (await chooser).setFiles({ name: fileName, mimeType: 'application/pdf', buffer: PDF });
    await page.getByRole('button', { name: `Upload ${fileName}` }).click();

    // "Download" is offered only for a CONFIRMED document: a row left
    // pending (the browser's PUT never reached S3) has no download control.
    const download = page.getByRole('button', { name: `Download ${fileName}` });
    await expect(
      download,
      'the upload should be confirmed — "Could not upload" with an unfinished row means the ' +
        "browser's PUT to the presigned URL failed: the bucket's CORS rule on staging, or " +
        "locally the compose stack's `blobs` service (backend/docker-compose.yml)",
    ).toBeVisible({ timeout: 30_000 });
    await expect(row(page, fileName)).toContainText('Uploaded and stored.');
    await expect(row(page, fileName)).toContainText('Bank statement');

    // ── The presigned GET hands back exactly what was sent ────────────────
    //
    // The app opens the URL in a new tab; `followCapability` reads it again
    // through the test's own request context (its header says why).
    const fetched = await followCapability(page, () => download.click());
    expect(fetched.status, 'the presigned GET should answer 200').toBe(200);
    expect(
      Buffer.compare(fetched.body, PDF) === 0,
      'the bytes the bucket returns should be the bytes the browser uploaded',
    ).toBe(true);

    // ── Delete it: gone, and still gone after a reload ────────────────────
    await page.getByRole('button', { name: `Delete ${fileName}` }).click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Delete document' }).click();
    await expect(row(page, fileName)).toHaveCount(0, { timeout: 15_000 });
    await page.reload();
    await expect(page.getByRole('heading', { level: 1, name: 'Case documents' })).toBeVisible();
    await expect(page.getByText('Loading…')).toHaveCount(0, { timeout: 15_000 });
    await expect(row(page, fileName)).toHaveCount(0);
  });
});
