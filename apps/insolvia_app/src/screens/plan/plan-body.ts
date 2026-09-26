import type {
  PlanBody,
  PlanCalculation,
  PlanSecuredTreatment,
  SecuredTreatmentKind,
} from '@insolvia-ai/api-client';

/**
 * Pure edits to the ONE `plans` record a case carries (issue 16.2 / #366),
 * and the words the plan screen puts on the server's verdicts.
 *
 * NOTHING HERE COMPUTES A FIGURE. The waterfall, feasibility and the
 * liquidation floor all come from `GET /v1/cases/{id}/plan-calculation`;
 * this module only shapes the request the next save (or the next scenario)
 * sends — ADR 0001, the client stays dumb.
 */

/** The stored record minus its identity — what a PUT sends back. */
export function planBodyOf(record: Record<string, unknown>): PlanBody {
  const {
    id: _id,
    case_id: _caseId,
    created_at: _created,
    updated_at: _updated,
    provenance: _provenance,
    ...body
  } = record;
  return body as PlanBody;
}

/** A blank text box is an absent value, never `""` on the wire. */
export function blankToAbsent(next: string): string | undefined {
  return next.trim() === '' ? undefined : next;
}

/** A whole-number box: digits only, blank is absent. */
export function wholeNumberOf(next: string): number | undefined {
  const digits = next.replaceAll(/[^0-9]/gu, '');
  return digits === '' ? undefined : Number(digits);
}

export function treatmentFor(body: PlanBody, claimId: string): PlanSecuredTreatment | undefined {
  return body.secured_treatments?.find((row) => row.claim_id === claimId);
}

/**
 * The record with one claim's treatment patched — minted as a new row
 * (`mintId`, the API's client-chosen id) the first time the claim is
 * touched. Choosing "no treatment" removes the row, so an untreated claim
 * never carries stale cramdown figures into the next save.
 */
export function withTreatment(
  body: PlanBody,
  claimId: string,
  patch: Partial<Omit<PlanSecuredTreatment, 'id' | 'claim_id'>>,
  mintId: () => string,
): PlanBody {
  const rows = body.secured_treatments ?? [];
  const existing = rows.find((row) => row.claim_id === claimId);
  if ('treatment' in patch && patch.treatment === undefined) {
    const rest = rows.filter((row) => row.claim_id !== claimId);
    return withList(body, 'secured_treatments', rest);
  }
  if (existing === undefined) {
    return {
      ...body,
      secured_treatments: [...rows, { id: mintId(), claim_id: claimId, ...patch }],
    };
  }
  return {
    ...body,
    secured_treatments: rows.map((row) => (row.claim_id === claimId ? { ...row, ...patch } : row)),
  };
}

/** Set an embedded list, dropping the key when it empties (the server's prune rule). */
export function withList<K extends 'secured_treatments' | 'step_payments' | 'lump_sums'>(
  body: PlanBody,
  key: K,
  rows: NonNullable<PlanBody[K]>,
): PlanBody {
  if (rows.length > 0) return { ...body, [key]: rows };
  const { [key]: _removed, ...without } = body;
  return without;
}

/** The fields each treatment asks for, in the order the screen shows them. */
export const TREATMENT_FIELDS: Readonly<
  Record<
    SecuredTreatmentKind,
    readonly { readonly key: keyof PlanSecuredTreatment; readonly label: string }[]
  >
> = {
  cure_and_maintain: [
    { key: 'arrearage', label: 'Arrearage cured through the plan' },
    { key: 'arrearage_interest_rate', label: 'Interest on the arrearage (% a year)' },
    {
      key: 'maintenance_payment',
      label: 'Ongoing monthly payment through the plan (blank: paid directly)',
    },
    { key: 'monthly_payment', label: 'Fixed monthly payment on the arrearage (blank: amortised)' },
  ],
  cramdown: [
    { key: 'cramdown_value', label: 'Value paid (blank: the derived secured portion)' },
    { key: 'interest_rate', label: 'Interest rate (% a year)' },
    { key: 'monthly_payment', label: 'Fixed monthly payment (blank: amortised)' },
  ],
  surrender: [],
};

export const TREATMENT_LABELS: Readonly<Record<SecuredTreatmentKind, string>> = {
  cure_and_maintain: 'Cure and maintain (§ 1322(b)(5))',
  cramdown: 'Cram down to value (§ 1325(a)(5)(B))',
  surrender: 'Surrender (§ 1325(a)(5)(C))',
};

/** A server verdict that may be undetermined, as a badge's words and intent. */
export interface Verdict {
  readonly label: string;
  readonly intent: 'success' | 'danger' | 'neutral';
}

export function feasibilityVerdict(calc: PlanCalculation): Verdict {
  const feasible = calc.feasibility.feasible;
  if (feasible === null) return { label: 'Not yet determined', intent: 'neutral' };
  return feasible
    ? { label: 'Feasible', intent: 'success' }
    : { label: 'Not feasible', intent: 'danger' };
}

export function bestInterestsVerdict(calc: PlanCalculation): Verdict {
  const passes = calc.bestInterests.passes;
  if (passes === null) return { label: 'Not yet determined', intent: 'neutral' };
  return passes
    ? { label: 'Meets the liquidation floor', intent: 'success' }
    : { label: 'Below the liquidation floor', intent: 'danger' };
}

/** A server percentage (`"47.50"`) as printed, or a dash while undetermined. */
export function percentText(value: string | null): string {
  return value === null ? '—' : `${value}%`;
}

/** A server money string as printed, or a dash while undetermined. */
export function moneyText(value: string | null): string {
  return value === null ? '—' : `$${value}`;
}

/** "Months 3–11", "Month 4" or "Not paid" for a class or a row. */
export function monthsText(first: number | null, last: number | null): string {
  if (first === null || last === null) return 'Not paid';
  return first === last ? `Month ${first}` : `Months ${first}–${last}`;
}
