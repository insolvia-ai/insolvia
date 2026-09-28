import { expect, type Page } from '@playwright/test';

/**
 * The browser suite's ONE scratch case, found or opened through the UI — the
 * only case a `flows` spec may write to.
 *
 * WHY THIS EXISTS. A signed-in environment is seeded with fixture cases
 * (`seeds/<target>.json`, ADR 0021), and the integration tier finds those by
 * their content — Debtor 1's name among it. `intake-persists.spec.ts` used to
 * open the FIRST case on `/cases` and type over Debtor 1's first name; since
 * ADR 0022 seeded clients, that first case is a fixture case, the loader
 * leaves an existing row alone, and the next release's integration tier could
 * not find the fixture it had just seeded. So: no spec writes to a case it did
 * not open itself, and this is the case it opens.
 *
 * ONE PER ENVIRONMENT, NOT ONE PER RUN. Neither cases nor firm clients can be
 * deleted through the API (a client can only be archived), so a spec that
 * opened a fresh pair on every staging deploy would grow a table nobody
 * prunes. The pair is found on later runs instead:
 *
 *   - the CLIENT by its exact name in the case form's client picker, which
 *     reads "Surname, Given";
 *   - the CASE by its district — a court no fixture case uses (they are all
 *     `flmb`) and the integration tier's own scratch case does not use either
 *     (`flnb`, `services/api/tests/integration/conftest.py`) — and then
 *     CONFIRMED by the case's heading, which names its debtors: Debtor 1's
 *     surname is the scratch client's, and no spec changes it. A case in the
 *     same district that is not ours is passed over, never written to.
 *
 * The two suites keep separate scratch cases on purpose: they run in
 * different jobs, and each rewrites Debtor 1.
 *
 * SELECTOR CONTRACT with `apps/insolvia_app` — role-based, by accessible name:
 *
 *   - case-list row link : "Chapter N case in <district>, opened …"
 *   - case heading       : the level-1 heading, the debtors' names
 *   - new-case form      : combobox "Client" (option "New client…", or the
 *     client as "Surname, Given"), textboxes "Client’s first name" /
 *     "Client’s last name", comboboxes "Court" and "Division", button
 *     "Open case"
 *
 * Nobody real is called this, and nothing here describes a person.
 */
export const SCRATCH_CLIENT = { given: 'Scratch', surname: 'E2E-Suite' } as const;
export const SCRATCH_DISTRICT = 'Southern District of Florida';
const SCRATCH_DIVISION = 'Miami Division';

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

const scratchCaseLinks = (page: Page) =>
  page.getByRole('link', {
    name: new RegExp(`^Chapter \\d+ case in ${escape(SCRATCH_DISTRICT)},`),
  });

/** `/cases`, with its list loaded — not "Loading your cases…". */
async function caseList(page: Page): Promise<void> {
  await page.goto('/cases');
  await expect(page.getByRole('heading', { level: 1, name: 'Your cases' })).toBeVisible();
  await expect(
    page.getByText('Loading your cases…'),
    'the case list should load — a stuck "Loading" means GET /v1/cases never answered',
  ).toHaveCount(0);
}

/**
 * On a case's overview: true when it is the scratch case. Waits for the
 * heading to name a person first — before the debtors load it falls back to
 * "Chapter N · District", which would say nothing about whose case it is.
 */
async function isScratchCase(page: Page): Promise<boolean> {
  const heading = page.getByRole('heading', { level: 1 }).first();
  await expect(
    heading,
    "the case's heading should name its debtors — every case has a Debtor 1 since ADR 0022",
  ).not.toHaveText(/^(Chapter \d+ · |Opening case)/);
  const text = (await heading.textContent()) ?? '';
  return text.includes(SCRATCH_CLIENT.surname);
}

/** Opens the case for the scratch client, choosing that client if it exists. */
async function openNewScratchCase(page: Page): Promise<void> {
  const clientPicker = page.getByRole('combobox', { name: 'Client' });
  await clientPicker.click();
  const existing = page.getByRole('option', {
    name: `${SCRATCH_CLIENT.surname}, ${SCRATCH_CLIENT.given}`,
    exact: true,
  });
  // A run that added the client and then failed before opening the case left
  // it in the directory; reuse it rather than adding a second one.
  if ((await existing.count()) > 0) {
    await existing.first().click();
  } else {
    await page.getByRole('option', { name: 'New client…' }).click();
    await page.getByRole('textbox', { name: 'Client’s first name' }).fill(SCRATCH_CLIENT.given);
    await page.getByRole('textbox', { name: 'Client’s last name' }).fill(SCRATCH_CLIENT.surname);
  }
  // Picked from the registry (`GET /v1/courts`, issue #360), never typed.
  await page.getByRole('combobox', { name: 'Court' }).click();
  await page.getByRole('option', { name: SCRATCH_DISTRICT, exact: true }).click();
  await page.getByRole('combobox', { name: 'Division' }).click();
  await page.getByRole('option', { name: SCRATCH_DIVISION, exact: true }).click();
  // The chapter radio is left at whatever the form offers; nothing reads it.
  await page.getByRole('button', { name: 'Open case' }).click();
  await expect(
    scratchCaseLinks(page).first(),
    'opening the scratch case should add it to the list — its absence means ' +
      'POST /v1/firm/clients or POST /v1/cases was refused',
  ).toBeVisible();
}

/**
 * Signed in, lands on the scratch case's overview — finding it, or opening it
 * (and its client) on the first run against an environment. The caller goes
 * on from the case's own rail.
 */
export async function openScratchCase(page: Page): Promise<void> {
  for (let attempt = 0; attempt < 2; attempt += 1) {
    await caseList(page);
    const count = await scratchCaseLinks(page).count();
    for (let index = 0; index < count; index += 1) {
      await scratchCaseLinks(page).nth(index).click();
      if (await isScratchCase(page)) return;
      await caseList(page);
    }
    if (attempt === 0) await openNewScratchCase(page);
  }
  throw new Error(
    `No case in ${SCRATCH_DISTRICT} names ${SCRATCH_CLIENT.given} ${SCRATCH_CLIENT.surname} ` +
      'as its debtor, even after opening one — refusing to write to any other case.',
  );
}
