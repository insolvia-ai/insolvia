import type {
  CaseMeansTest,
  IncomeColumn,
  IncomeLineCategory,
  IncomeLineOverride,
  MeansTestInputBody,
  OtherSecuredPayment,
} from '@insolvia-ai/api-client';

/**
 * Pure edits to the ONE `means_test_input` record a case carries (issue
 * #349), shared by the means-test screen and the secured-payment panel
 * beside a claim. Both write the whole record back through the generic
 * collection route, so what they must agree on is how a row is keyed:
 *
 * - an income override is one row per column per line, addressed by
 *   `(column, category)`; setting a blank amount removes the row rather than
 *   storing an empty override;
 * - a per-claim secured payment is one row per `claim_id`; the engine reads
 *   it by `bucket`, the panel finds it by claim.
 *
 * NOTHING HERE COMPUTES A FIGURE. The verdict, the CMI lines and the
 * B122A-2 lines all come from `GET /v1/cases/{id}/means-test`; this module
 * only shapes the request the next save sends (ADR 0001 — the client stays
 * dumb).
 */

/** The stored record minus its identity — what a PUT sends back. */
export function inputBodyOf(record: Record<string, unknown>): MeansTestInputBody {
  const {
    id: _id,
    case_id: _caseId,
    created_at: _created,
    updated_at: _updated,
    provenance: _provenance,
    ...body
  } = record;
  return body as MeansTestInputBody;
}

export function overrideFor(
  body: MeansTestInputBody,
  column: IncomeColumn,
  category: IncomeLineCategory,
): IncomeLineOverride | undefined {
  return body.income_overrides?.find((row) => row.column === column && row.category === category);
}

/**
 * The record with one column's line overridden by `monthlyAmount`, or with
 * that override removed when the amount is blank. `mintId` names a new row
 * (the API requires a client-chosen id so provenance can address it).
 */
export function withOverride(
  body: MeansTestInputBody,
  column: IncomeColumn,
  category: IncomeLineCategory,
  monthlyAmount: string | undefined,
  mintId: () => string,
): MeansTestInputBody {
  const others = (body.income_overrides ?? []).filter(
    (row) => !(row.column === column && row.category === category),
  );
  const trimmed = monthlyAmount?.trim() ?? '';
  if (trimmed === '') {
    return others.length === 0
      ? withoutKey(body, 'income_overrides')
      : { ...body, income_overrides: others };
  }
  const existing = overrideFor(body, column, category);
  const row: IncomeLineOverride = {
    id: existing?.id ?? mintId(),
    column,
    category,
    monthly_amount: trimmed,
  };
  return { ...body, income_overrides: [...others, row] };
}

export function securedPaymentFor(
  body: MeansTestInputBody,
  claimId: string,
): OtherSecuredPayment | undefined {
  return body.other_secured_payments?.find((row) => row.claim_id === claimId);
}

/** The record with the row for `row.claim_id` replaced (or added). */
export function withSecuredPayment(
  body: MeansTestInputBody,
  row: OtherSecuredPayment,
): MeansTestInputBody {
  const others = (body.other_secured_payments ?? []).filter(
    (candidate) => candidate.id !== row.id && candidate.claim_id !== row.claim_id,
  );
  return { ...body, other_secured_payments: [...others, row] };
}

/** The record without any row entered beside `claimId`. */
export function withoutSecuredPayment(
  body: MeansTestInputBody,
  claimId: string,
): MeansTestInputBody {
  const others = (body.other_secured_payments ?? []).filter((row) => row.claim_id !== claimId);
  return others.length === 0
    ? withoutKey(body, 'other_secured_payments')
    : { ...body, other_secured_payments: others };
}

function withoutKey(body: MeansTestInputBody, key: keyof MeansTestInputBody): MeansTestInputBody {
  const { [key]: _removed, ...rest } = body;
  return rest;
}

/**
 * The one-line verdict the pinned banner prints for a trace, in the order
 * the issue names them: CMI, median, under/over, presumption. Text only —
 * every figure is the server's, unformatted beyond a `$`.
 */
export interface Verdict {
  readonly cmi: string;
  readonly median: string;
  readonly position: string;
  readonly presumption: string;
  readonly intent: 'neutral' | 'success' | 'warning' | 'danger';
}

export function verdictOf(trace: CaseMeansTest): Verdict {
  const cmi = `$${trace.cmi.combinedMonthlyTotal}`;
  const comparison = trace.comparison;
  const median = comparison === null ? '—' : `$${comparison.annualMedian}`;
  const position =
    comparison === null
      ? 'Comparison not yet made'
      : comparison.aboveMedian
        ? 'Above the median'
        : 'At or below the median';
  switch (trace.outcome) {
    case 'below_median':
      return { cmi, median, position, presumption: 'No presumption of abuse', intent: 'success' };
    case 'no_presumption':
      return { cmi, median, position, presumption: 'No presumption of abuse', intent: 'success' };
    case 'presumption_of_abuse':
      return {
        cmi,
        median,
        position,
        presumption: 'Presumption of abuse arises',
        intent: 'danger',
      };
    case 'exempt':
      return {
        cmi,
        median,
        position,
        presumption: 'Exempt — the test does not apply',
        intent: 'success',
      };
    case 'undetermined':
      return { cmi, median, position, presumption: 'Not yet determined', intent: 'warning' };
  }
}
