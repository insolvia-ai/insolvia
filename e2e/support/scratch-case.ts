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
 * prunes. The pair is found on later runs instead — THROUGH THE CLIENT LIST,
 * the app's front door since #354:
 *
 *   - the CLIENT by searching `/clients` for its surname and taking the row
 *     whose link reads exactly "Surname, Given";
 *   - the CASE on that client's record, by its district — a court no fixture
 *     case uses (they are all `flmb`) and the integration tier's own scratch
 *     case does not use either (`flnb`,
 *     `services/api/tests/integration/conftest.py`) — and then CONFIRMED by
 *     the case's heading, which names its debtors: Debtor 1's surname is the
 *     scratch client's, and no spec changes it. A case that is not ours is
 *     passed over, never written to.
 *
 * A first run against an environment adds the client ("Add client" → "Save
 * and start a case") or, when an earlier run added the client and failed
 * before its case, starts one from the record ("Start a case for this
 * client"). Either way the case form arrives with the client already chosen.
 *
 * The two suites keep separate scratch cases on purpose: they run in
 * different jobs, and each rewrites Debtor 1.
 *
 * SELECTOR CONTRACT with `apps/insolvia_app` — role-based, by accessible name:
 *
 *   - client list      : heading "Clients"; textbox "Search clients"; each
 *     client a link "Surname, Given"; button "Add client"
 *   - add client       : textboxes "First name" / "Last name"; button "Save
 *     and start a case"
 *   - client record    : level-1 heading "Given Surname"; each reachable case
 *     a link "Chapter N case in <district>, opened <date>"; button "Start a
 *     case for this client"
 *   - new-case form    : comboboxes "Court" and "Division"; button "Open case"
 *     (it lands on the new case)
 *   - case heading     : the level-1 heading, the debtors' names
 *
 * Nobody real is called this, and nothing here describes a person.
 */
export const SCRATCH_CLIENT = { given: 'Scratch', surname: 'E2E-Suite' } as const;
export const SCRATCH_DISTRICT = 'Southern District of Florida';
const SCRATCH_DIVISION = 'Miami Division';

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** A case link as the client list and the client record both name it. */
export const scratchCaseLinks = (page: Page) =>
  page.getByRole('link', {
    name: new RegExp(`^Chapter \\d+ case in ${escape(SCRATCH_DISTRICT)},`),
  });

/** The scratch client's rows on `/clients` — normally one. */
export const scratchClientLinks = (page: Page) =>
  page.getByRole('link', {
    name: `${SCRATCH_CLIENT.surname}, ${SCRATCH_CLIENT.given}`,
    exact: true,
  });

/** `/clients`, loaded and searched down to the scratch client's surname. */
export async function searchClientList(page: Page): Promise<void> {
  await page.goto('/clients');
  await expect(page.getByRole('heading', { level: 1, name: 'Clients' })).toBeVisible();
  await expect(
    page.getByText('Loading your clients…'),
    'the client list should load — a stuck "Loading" means GET /v1/firm/clients never ' +
      'answered, and a "not given you access" means the test user lacks `clients`',
  ).toHaveCount(0);
  const search = page.getByRole('textbox', { name: 'Search clients' });
  // An empty directory has no search box — only "No clients yet".
  if ((await search.count()) > 0) await search.fill(SCRATCH_CLIENT.surname);
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

/** On `/cases/new` with the client already chosen: the court, then open. */
async function openCaseInScratchDistrict(page: Page): Promise<void> {
  await expect(page.getByRole('heading', { level: 1, name: 'Open a case' })).toBeVisible();
  await expect(
    page.getByRole('combobox', { name: 'Client' }),
    'the new-case form should arrive with the scratch client already chosen',
  ).toContainText(`${SCRATCH_CLIENT.surname}, ${SCRATCH_CLIENT.given}`);
  // Picked from the registry (`GET /v1/courts`, issue #360), never typed.
  await page.getByRole('combobox', { name: 'Court' }).click();
  await page.getByRole('option', { name: SCRATCH_DISTRICT, exact: true }).click();
  await page.getByRole('combobox', { name: 'Division' }).click();
  await page.getByRole('option', { name: SCRATCH_DIVISION, exact: true }).click();
  // The chapter radio is left at whatever the form offers; nothing reads it.
  await page.getByRole('button', { name: 'Open case' }).click();
  await page.waitForURL(/\/cases\/(?!new)[^/]+$/, { timeout: 30_000 });
}

/**
 * On the scratch client's record: opens its scratch case if it has one and
 * returns true, having landed on the case. False when the record lists no
 * case of ours.
 */
async function openFromRecord(page: Page): Promise<boolean> {
  await expect(
    page.getByRole('heading', {
      level: 1,
      name: `${SCRATCH_CLIENT.given} ${SCRATCH_CLIENT.surname}`,
    }),
  ).toBeVisible();
  await expect(page.getByText('Loading their cases…')).toHaveCount(0);
  const record = page.url();
  const count = await scratchCaseLinks(page).count();
  for (let index = 0; index < count; index += 1) {
    await scratchCaseLinks(page).nth(index).click();
    if (await isScratchCase(page)) return true;
    await page.goto(record);
    await expect(page.getByText('Loading their cases…')).toHaveCount(0);
  }
  return false;
}

/**
 * Signed in, lands on the scratch case's overview — found through the client
 * list, or opened (with its client, if need be) on the first run against an
 * environment. The caller goes on from the case's own rail.
 */
export async function openScratchCase(page: Page): Promise<void> {
  await searchClientList(page);
  const rows = await scratchClientLinks(page).count();

  if (rows === 0) {
    await page.getByRole('button', { name: 'Add client' }).click();
    await page.getByRole('textbox', { name: 'First name' }).fill(SCRATCH_CLIENT.given);
    await page.getByRole('textbox', { name: 'Last name' }).fill(SCRATCH_CLIENT.surname);
    await page.getByRole('button', { name: 'Save and start a case' }).click();
    await openCaseInScratchDistrict(page);
  } else {
    // More than one row means an earlier run added the client twice; any of
    // them may hold the case, and the first with one wins.
    for (let index = 0; index < rows; index += 1) {
      await scratchClientLinks(page).nth(index).click();
      if (await openFromRecord(page)) return;
      await searchClientList(page);
    }
    // The client exists but no case of ours does: start one from its record.
    await scratchClientLinks(page).first().click();
    await page.getByRole('button', { name: 'Start a case for this client' }).click();
    await openCaseInScratchDistrict(page);
  }

  if (!(await isScratchCase(page))) {
    throw new Error(
      `The case just opened in ${SCRATCH_DISTRICT} does not name ` +
        `${SCRATCH_CLIENT.given} ${SCRATCH_CLIENT.surname} as its debtor — refusing to write to it.`,
    );
  }
}
