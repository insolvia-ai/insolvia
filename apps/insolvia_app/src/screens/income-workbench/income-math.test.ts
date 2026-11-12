import type { IncomeSummaryBody, PayPeriodRecordBody } from '@insolvia-ai/api-client';

import { computeScheduleILines, summarizePayRecordsByEmployer } from './income-math';

describe('computeScheduleILines', () => {
  // Same scenario b106i.py's own known-answer tests use, redone here so the
  // preview and the filed figure cannot silently diverge.
  test('derives lines 4, 6, 7, 9 and 10 from an entered summary', () => {
    const summary: IncomeSummaryBody = {
      wages: 3000,
      overtime: 200,
      deductionTax: 500,
      deductionInsurance: 100,
      businessNetIncome: 150,
    };

    const lines = computeScheduleILines(summary);

    expect(lines.grossIncome).toBe('3200.00');
    expect(lines.totalDeductions).toBe('600.00');
    expect(lines.takeHomePay).toBe('2600.00');
    expect(lines.totalOtherIncome).toBe('150.00');
    expect(lines.monthlyIncome).toBe('2750.00');
  });

  test('an entirely empty summary computes to all zeros, not a blank screen', () => {
    const lines = computeScheduleILines({});

    expect(lines).toEqual({
      grossIncome: '0.00',
      totalDeductions: '0.00',
      takeHomePay: '0.00',
      totalOtherIncome: '0.00',
      monthlyIncome: '0.00',
    });
  });

  test('deductions exceeding gross income produce a negative take-home, not a floor', () => {
    // 106I has no zero floor on line 7 the way B122A-1's business/rental
    // lines do — a debtor whose deductions exceed gross pay is a fact this
    // preview must show, not hide.
    const lines = computeScheduleILines({ wages: 100, deductionTax: 400 });

    expect(lines.takeHomePay).toBe('-300.00');
  });
});

describe('summarizePayRecordsByEmployer', () => {
  function record(overrides: PayPeriodRecordBody): PayPeriodRecordBody {
    return { employmentId: 'em-1', frequency: 'monthly', ...overrides };
  }

  test('groups records by employmentId', () => {
    const rows = [
      record({ employmentId: 'em-1', gross: 1000, net: 800 }),
      record({ employmentId: 'em-2', gross: 2000, net: 1600 }),
    ];

    const summary = summarizePayRecordsByEmployer(rows);

    expect(summary).toHaveLength(2);
    expect(summary.map((row) => row.employmentId).sort()).toEqual(['em-1', 'em-2']);
  });

  test('averages gross and net across an employer’s records', () => {
    const rows = [record({ gross: 1000, net: 800 }), record({ gross: 1200, net: 900 })];

    const [summary] = summarizePayRecordsByEmployer(rows);

    expect(summary?.recordCount).toBe(2);
    expect(summary?.averageGross).toBe('1100.00');
    expect(summary?.averageNet).toBe('850.00');
    expect(summary?.totalGross).toBe('2200.00');
  });

  test('a record naming no employer is excluded rather than grouped under "undefined"', () => {
    const rows = [record({ employmentId: undefined, gross: 500 })];

    expect(summarizePayRecordsByEmployer(rows)).toEqual([]);
  });

  test('a record with no gross or net figure counts toward the average as zero', () => {
    const rows = [record({ gross: 1000 }), record({ gross: undefined, net: undefined })];

    const [summary] = summarizePayRecordsByEmployer(rows);

    expect(summary?.recordCount).toBe(2);
    expect(summary?.averageGross).toBe('500.00');
  });
});
