import type { PlanBody } from '@insolvia-ai/api-client';

import {
  blankToAbsent,
  monthsText,
  treatmentFor,
  wholeNumberOf,
  withList,
  withTreatment,
} from './plan-body';

const mint = () => 'row-1';

describe('the plan record edits', () => {
  it('mints a treatment row the first time a claim is treated', () => {
    const body = withTreatment({}, 'claim-car', { treatment: 'cramdown' }, mint);

    expect(body.securedTreatments).toEqual([
      { id: 'row-1', claimId: 'claim-car', treatment: 'cramdown' },
    ]);
  });

  it('patches the existing row rather than adding a second', () => {
    const start = withTreatment({}, 'claim-car', { treatment: 'cramdown' }, mint);

    const body = withTreatment(start, 'claim-car', { interestRate: 7 }, () => 'row-2');

    expect(body.securedTreatments).toEqual([
      { id: 'row-1', claimId: 'claim-car', treatment: 'cramdown', interestRate: 7 },
    ]);
    expect(treatmentFor(body, 'claim-car')?.interestRate).toBe(7);
  });

  it('choosing no treatment removes the row, and the empty list with it', () => {
    const start = withTreatment({}, 'claim-car', { treatment: 'cramdown' }, mint);

    const body = withTreatment(start, 'claim-car', { treatment: undefined }, mint);

    expect(body).toEqual({});
  });

  it('an emptied list is dropped, never sent as []', () => {
    const body: PlanBody = { lumpSums: [{ id: 'l1' }], monthlyPayment: 1 };

    expect(withList(body, 'lumpSums', [])).toEqual({ monthlyPayment: 1 });
  });

  it.each([
    ['', undefined],
    ['  ', undefined],
    ['500', '500'],
  ])('a blank box is absent: %j', (given, expected) => {
    expect(blankToAbsent(given)).toBe(expected);
  });

  it('a whole-number box keeps digits only', () => {
    expect(wholeNumberOf('36 months')).toBe(36);
    expect(wholeNumberOf('')).toBeUndefined();
  });

  it.each([
    [1, 11, 'Months 1–11'],
    [4, 4, 'Month 4'],
    [null, null, 'Not paid'],
  ])('names the months %j to %j', (first, last, text) => {
    expect(monthsText(first, last)).toBe(text);
  });
});
