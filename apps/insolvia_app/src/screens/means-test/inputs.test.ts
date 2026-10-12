import type { CaseMeansTest, MeansTestInputBody } from '@insolvia-ai/api-client';

import {
  inputBodyOf,
  overrideFor,
  securedPaymentFor,
  verdictOf,
  withOverride,
  withSecuredPayment,
  withoutSecuredPayment,
} from './inputs';

const mint = () => 'minted';

describe('inputBodyOf', () => {
  it('strips the record identity and keeps the answers', () => {
    expect(
      inputBodyOf({
        id: 'x',
        case_id: 'c',
        created_at: 't',
        updated_at: 't',
        provenance: {},
        taxes: '10.00',
      }),
    ).toEqual({ taxes: '10.00' });
  });
});

describe('income overrides', () => {
  it('adds a row keyed by column and line, minting its id', () => {
    const next = withOverride({}, 'A', 'wages', '5000.00', mint);
    expect(next.income_overrides).toEqual([
      { id: 'minted', column: 'A', category: 'wages', monthly_amount: '5000.00' },
    ]);
    expect(overrideFor(next, 'A', 'wages')?.monthly_amount).toBe('5000.00');
  });

  it('replaces an existing row in place, keeping its id', () => {
    const body: MeansTestInputBody = {
      income_overrides: [
        { id: 'keep', column: 'A', category: 'wages', monthly_amount: '1.00' },
        { id: 'other', column: 'B', category: 'wages', monthly_amount: '2.00' },
      ],
    };
    const next = withOverride(body, 'A', 'wages', '3.00', mint);
    expect(next.income_overrides).toEqual([
      { id: 'other', column: 'B', category: 'wages', monthly_amount: '2.00' },
      { id: 'keep', column: 'A', category: 'wages', monthly_amount: '3.00' },
    ]);
  });

  it('a blank amount removes the row, and the list when it was the last', () => {
    const body: MeansTestInputBody = {
      taxes: '10.00',
      income_overrides: [{ id: 'keep', column: 'A', category: 'wages', monthly_amount: '1.00' }],
    };
    expect(withOverride(body, 'A', 'wages', '  ', mint)).toEqual({ taxes: '10.00' });
    expect(withOverride(body, 'B', 'wages', undefined, mint)).toEqual(body);
  });
});

describe('secured payments', () => {
  it('upserts by claim and finds the row by claim', () => {
    const first = withSecuredPayment(
      {},
      { id: 'r1', claim_id: 'claim-1', bucket: 'home', monthly_payment: '1200.00' },
    );
    const second = withSecuredPayment(first, {
      id: 'r1',
      claim_id: 'claim-1',
      bucket: 'vehicle_1',
      monthly_payment: '400.00',
    });
    expect(second.other_secured_payments).toHaveLength(1);
    expect(securedPaymentFor(second, 'claim-1')?.bucket).toBe('vehicle_1');
  });

  it('leaves rows entered by hand (no claim) alone', () => {
    const body: MeansTestInputBody = {
      other_secured_payments: [{ id: 'typed', creditor_name: 'Marina', monthly_payment: '1.00' }],
    };
    const next = withSecuredPayment(body, { id: 'r1', claim_id: 'claim-1', bucket: 'other' });
    expect(next.other_secured_payments?.map((row) => row.id)).toEqual(['typed', 'r1']);
    expect(withoutSecuredPayment(next, 'claim-1')).toEqual(body);
    expect(
      withoutSecuredPayment({ other_secured_payments: [{ id: 'r1', claim_id: 'c' }] }, 'c'),
    ).toEqual({});
  });
});

describe('verdictOf', () => {
  const base = {
    chapter: 7,
    cmi: { combinedMonthlyTotal: '8700.00' },
    comparison: { annualMedian: '97540.00', aboveMedian: true },
    chapter13: null,
  } as unknown as CaseMeansTest;

  it('reads every figure from the trace and never computes one', () => {
    const verdict = verdictOf({ ...base, outcome: 'presumption_of_abuse' } as CaseMeansTest);
    expect(verdict).toEqual({
      cmi: '$8700.00',
      median: '$97540.00',
      position: 'Above the median',
      determinationLabel: 'Presumption of abuse',
      presumption: 'Presumption of abuse arises',
      intent: 'danger',
      chapter13: null,
    });
  });

  it('reads the Chapter 13 verdict — commitment period and disposable income — verbatim', () => {
    const verdict = verdictOf({
      ...base,
      chapter: 13,
      outcome: 'above_median',
      chapter13: {
        commitmentPeriodMonths: 60,
        commitmentSource: 'x',
        commitmentMaritalAdjustment: '0.00',
        commitmentMonthlyIncome: '8700.00',
        commitmentAnnualizedIncome: '104400.00',
        commitmentAnnualMedian: '97540.00',
        disposableIncomeRequired: true,
        monthlyDisposableIncome: '-1079.33',
      },
    } as CaseMeansTest);
    expect(verdict.determinationLabel).toBe('Disposable income under § 1325(b)(3)');
    expect(verdict.presumption).toMatch(/Form 122C-2 is required/u);
    expect(verdict.intent).toBe('warning');
    expect(verdict.chapter13).toEqual({
      commitmentPeriod: '60 months',
      disposableIncome: '$-1079.33',
    });
  });

  it('says a below-median Chapter 13 debtor files no 122C-2 and keeps the period', () => {
    const verdict = verdictOf({
      ...base,
      chapter: 13,
      comparison: { annualMedian: '97540.00', aboveMedian: false },
      outcome: 'below_median',
      chapter13: {
        commitmentPeriodMonths: 36,
        commitmentSource: 'x',
        commitmentMaritalAdjustment: '0.00',
        commitmentMonthlyIncome: '5800.00',
        commitmentAnnualizedIncome: '69600.00',
        commitmentAnnualMedian: '97540.00',
        disposableIncomeRequired: false,
        monthlyDisposableIncome: null,
      },
    } as CaseMeansTest);
    expect(verdict.presumption).toMatch(/Form 122C-2 is not filed/u);
    expect(verdict.intent).toBe('success');
    expect(verdict.chapter13).toEqual({
      commitmentPeriod: '36 months',
      disposableIncome: 'Not determined under § 1325(b)(3)',
    });
  });

  it('says so while the test cannot be determined', () => {
    const verdict = verdictOf({
      ...base,
      comparison: null,
      outcome: 'undetermined',
    } as CaseMeansTest);
    expect(verdict.median).toBe('—');
    expect(verdict.position).toBe('Comparison not yet made');
    expect(verdict.intent).toBe('warning');
  });

  it('names an exemption as the end of the test', () => {
    expect(verdictOf({ ...base, outcome: 'exempt' } as CaseMeansTest).presumption).toMatch(
      /Exempt/u,
    );
  });
});
