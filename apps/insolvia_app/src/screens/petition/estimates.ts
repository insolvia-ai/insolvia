import { ESTIMATED_CREDITORS_BANDS, ESTIMATED_DOLLAR_BANDS } from '@insolvia-ai/api-client';
import type { EstimatedCreditorsBand, EstimatedDollarBand } from '@insolvia-ai/api-client';

/**
 * Maps a schedule-derived count or total onto B101's own printed brackets
 * (issue #342) — Part 6's three "estimated" lines, which the form has the
 * debtor self-select rather than compute. `services/api/.../core/
 * form_projections/b101.py` projects these bands by ORDER against the same
 * two arrays this module imports from the api-client (which mirror
 * `core/petitions.py` member for member), so a bracket found here is always
 * one the projection accepts.
 *
 * A PURE MODULE, deliberately: the petition screen derives a default from the
 * case's own creditor count and summary totals, and this is the arithmetic
 * that turns a number into a bracket — testable without a screen, an API
 * client, or a case.
 */

interface Bracket<Band extends string> {
  readonly band: Band;
  readonly min: number;
  readonly max: number;
}

/** `"1_49"` → {min: 1, max: 49}; `"more_than_100000"` → {min: 100001, max: Infinity}. */
function brackets<Band extends string>(bands: readonly Band[]): readonly Bracket<Band>[] {
  return bands.map((band) => {
    if (band.startsWith('more_than_')) {
      const min = Number(band.slice('more_than_'.length)) + 1;
      return { band, min, max: Number.POSITIVE_INFINITY };
    }
    const [minText, maxText] = band.split('_');
    return { band, min: Number(minText), max: Number(maxText) };
  });
}

const CREDITOR_BRACKETS = brackets(ESTIMATED_CREDITORS_BANDS);
const DOLLAR_BRACKETS = brackets(ESTIMATED_DOLLAR_BANDS);

/**
 * The last bracket whose `min` is at or below `value` — the printed
 * brackets are contiguous and ascending, so this is the one the form's own
 * whole-dollar scale means. Matters for money: a total like `$50,000.01`
 * falls between one bracket's whole-dollar `max` and the next one's `min`
 * (the brackets are printed in whole dollars; a total is not), and comparing
 * against `max` as well would put it in neither. Walking by `min` alone
 * rounds a fractional amount down into the bracket it is closest to without
 * needing a special case for the gap.
 */
function bandFor<Band extends string>(
  ordered: readonly Bracket<Band>[],
  value: number,
): Band | undefined {
  let matched: Band | undefined;
  for (const bracket of ordered) {
    if (value < bracket.min) break;
    matched = bracket.band;
  }
  return matched;
}

/**
 * Line 18's bracket for a creditor count. The form's lowest bracket starts at
 * 1 — there is no "0 creditors" box — so a case with none recorded yet has no
 * derived band, `undefined`, rather than a guess at the bottom bracket.
 */
export function creditorsBand(count: number): EstimatedCreditorsBand | undefined {
  if (!Number.isFinite(count)) return undefined;
  return bandFor(CREDITOR_BRACKETS, count);
}

/**
 * Lines 19/20's shared dollar bracket for a total. Accepts the decimal
 * STRING `GET /v1/cases/{id}/summary` returns ({@link CaseTotals}) as well as
 * a plain number, so a caller need not parse first. `undefined` for anything
 * that is not a finite, non-negative amount — an unreadable total derives
 * nothing rather than defaulting to the lowest bracket.
 */
export function dollarBand(amount: string | number): EstimatedDollarBand | undefined {
  const value = typeof amount === 'string' ? Number.parseFloat(amount) : amount;
  if (!Number.isFinite(value) || value < 0) return undefined;
  return bandFor(DOLLAR_BRACKETS, value);
}
