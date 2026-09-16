/**
 * The two statutory dates the petition screen computes from the case's
 * expected filing date (issue #342). Neither is stored — they are arithmetic
 * over `PetitionBody.expected_filing_date`, recomputed whenever that date
 * changes, the same "derived values are computed, never stored" rule
 * docs/reference/case-data-model.md states for every schedule total.
 *
 * A SMALL PURE MODULE ON PURPOSE: date arithmetic that reaches across a leap
 * year is exactly the kind of thing that is easy to get subtly wrong once and
 * never notice, because the wrong answer is still a plausible-looking date.
 * Kept dependency-free (no date library) and UTC throughout, so a browser in
 * any timezone computes the same calendar day the form prints.
 */

const ISO_DATE_RE = /^(\d{4})-(\d{2})-(\d{2})$/u;

/** The §109(h) pre-filing counseling window is 180 days. */
const COUNSELING_WINDOW_DAYS = 180;

/** §727(a)(8) (chapter 7 after a prior chapter 7) and §1328(f) (chapter 13
 * after a prior chapter 7/11/12) both use an 8-year lookback — the longer of
 * the Code's discharge bars, and the one line 9 of B101 itself asks about. */
const PRIOR_CASE_LOOKBACK_YEARS = 8;

export interface StatutoryDates {
  /** The earliest date a counseling briefing counts for this filing —
   * 180 days before the expected filing date. The filing date itself is the
   * window's other, later end. */
  readonly counselingWindowStart: string;
  /** Prior cases filed on or after this date fall inside the 8-year
   * lookback and are what B101 line 9 is actually asking about. */
  readonly priorCaseLookbackCutoff: string;
}

/**
 * Parses a strict `YYYY-MM-DD` calendar date into a UTC midnight `Date`, or
 * `null` for anything malformed — including a real-looking date that is not
 * one (`2019-02-30`), the same rule `core/fields.py`'s `form_date` enforces
 * server-side. Never trust `Date`'s own lenient parsing: it rolls an invalid
 * day into the next month instead of rejecting it.
 */
function parseFormDate(value: string): Date | null {
  const match = ISO_DATE_RE.exec(value);
  if (match === null) return null;
  const [, yearText, monthText, dayText] = match;
  const year = Number(yearText);
  const month = Number(monthText);
  const day = Number(dayText);
  const parsed = new Date(Date.UTC(year, month - 1, day));
  const roundTrips =
    parsed.getUTCFullYear() === year &&
    parsed.getUTCMonth() === month - 1 &&
    parsed.getUTCDate() === day;
  return roundTrips ? parsed : null;
}

function toFormDate(date: Date): string {
  const year = String(date.getUTCFullYear()).padStart(4, '0');
  const month = String(date.getUTCMonth() + 1).padStart(2, '0');
  const day = String(date.getUTCDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

/**
 * The two statutory dates, or `null` when `expectedFilingDate` is absent or
 * malformed — a screen with nothing to compute from shows nothing computed,
 * rather than a date arithmetic on today's date would invent.
 */
export function statutoryDates(expectedFilingDate: string | undefined): StatutoryDates | null {
  if (expectedFilingDate === undefined) return null;
  const filing = parseFormDate(expectedFilingDate);
  if (filing === null) return null;

  const counselingStart = new Date(filing.getTime());
  counselingStart.setUTCDate(counselingStart.getUTCDate() - COUNSELING_WINDOW_DAYS);

  const lookbackCutoff = new Date(filing.getTime());
  lookbackCutoff.setUTCFullYear(lookbackCutoff.getUTCFullYear() - PRIOR_CASE_LOOKBACK_YEARS);

  return {
    counselingWindowStart: toFormDate(counselingStart),
    priorCaseLookbackCutoff: toFormDate(lookbackCutoff),
  };
}

/**
 * A stored `YYYY-MM-DD` as a reading date — `formatFormDate('2026-06-15')` →
 * `"June 15, 2026"`. Falls back to the raw value for anything malformed
 * rather than throwing: a display helper should never take a screen down.
 */
export function formatFormDate(value: string): string {
  const parsed = parseFormDate(value);
  if (parsed === null) return value;
  return new Intl.DateTimeFormat('en-US', {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
    timeZone: 'UTC',
  }).format(parsed);
}
