import type { IncomeSummaryBody, PayPeriodRecordBody } from '@insolvia-ai/api-client';

import { fromCents, sumMoney, toCents } from './money';

/**
 * Schedule I's own arithmetic (issue #348), computed here so the screen can
 * show it as the preparer types — not stored, and not re-derived ad hoc: this
 * mirrors `services/api/.../core/form_projections/b106i.py`'s
 * `project_b106i_1215` line for line (lines 2-10), and nothing here invents
 * arithmetic that module does not already define. The server remains the
 * only place these figures are FILED — `income_summary` is entered and
 * confirmed, never computed, on write (case-data-model.md); this is a
 * read-only preview.
 */

const DEDUCTION_KEYS = [
  'deduction_tax',
  'deduction_mandatory_retirement',
  'deduction_voluntary_retirement',
  'deduction_retirement_loan_repayment',
  'deduction_insurance',
  'deduction_domestic_support',
  'deduction_union_dues',
  'deduction_other',
] as const satisfies readonly (keyof IncomeSummaryBody)[];

const OTHER_INCOME_KEYS = [
  'business_net_income',
  'interest_and_dividends',
  'family_support',
  'unemployment',
  'social_security',
  'other_government_assistance',
  'pension_or_retirement',
  'other_monthly_income',
] as const satisfies readonly (keyof IncomeSummaryBody)[];

export interface ScheduleILines {
  /** Line 4 — wages plus overtime. */
  readonly grossIncome: string;
  /** Line 6 — the sum of deduction lines 5a-5h. */
  readonly totalDeductions: string;
  /** Line 7 — gross income minus total deductions; "net per period" reads
   * this as the current month's take-home, the same way the form does. */
  readonly takeHomePay: string;
  /** Line 9 — the sum of other-income lines 8a-8h. */
  readonly totalOtherIncome: string;
  /** Line 10 — take-home pay plus total other income. */
  readonly monthlyIncome: string;
}

export function computeScheduleILines(summary: IncomeSummaryBody): ScheduleILines {
  const grossCents = toCents(summary.wages) + toCents(summary.overtime);
  const deductionsCents = DEDUCTION_KEYS.reduce((total, key) => total + toCents(summary[key]), 0);
  const takeHomeCents = grossCents - deductionsCents;
  const otherIncomeCents = OTHER_INCOME_KEYS.reduce(
    (total, key) => total + toCents(summary[key]),
    0,
  );
  return {
    grossIncome: fromCents(grossCents),
    totalDeductions: fromCents(deductionsCents),
    takeHomePay: fromCents(takeHomeCents),
    totalOtherIncome: fromCents(otherIncomeCents),
    monthlyIncome: fromCents(takeHomeCents + otherIncomeCents),
  };
}

/**
 * A per-employer summary of the hand-entered (or extracted) pay-period rows
 * on file — "how the rows average" (issue #348). This is a plain arithmetic
 * mean of whatever records exist, NOT the § 101(10A) six-month current
 * monthly income the means test computes (`core/cmi.py`): CMI applies its
 * own dated lookback window and floor rules, and lives entirely server-side.
 * This is a quick read of the data as entered, for the preparer to sanity
 * check while typing — never sent anywhere or filed.
 */
export interface EmployerPaySummary {
  readonly employmentId: string;
  readonly recordCount: number;
  readonly averageGross: string;
  readonly averageNet: string;
  readonly totalGross: string;
}

export function summarizePayRecordsByEmployer(
  records: readonly (PayPeriodRecordBody & { readonly id?: string })[],
): readonly EmployerPaySummary[] {
  const byEmployer = new Map<string, PayPeriodRecordBody[]>();
  for (const record of records) {
    const employmentId = record.employment_id;
    if (employmentId === undefined) continue;
    const bucket = byEmployer.get(employmentId) ?? [];
    bucket.push(record);
    byEmployer.set(employmentId, bucket);
  }
  return [...byEmployer.entries()].map(([employmentId, rows]) => {
    const totalGrossCents = rows.reduce((total, row) => total + toCents(row.gross), 0);
    const totalNetCents = rows.reduce((total, row) => total + toCents(row.net), 0);
    return {
      employmentId,
      recordCount: rows.length,
      totalGross: fromCents(totalGrossCents),
      averageGross: fromCents(Math.round(totalGrossCents / rows.length)),
      averageNet: fromCents(Math.round(totalNetCents / rows.length)),
    };
  });
}

/** Re-exported for the screen's total-received line, computed the same
 * exact way as every other sum here. */
export { sumMoney };
