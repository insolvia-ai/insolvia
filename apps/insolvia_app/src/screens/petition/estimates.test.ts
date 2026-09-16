import { creditorsBand, dollarBand } from './estimates';

describe('creditorsBand', () => {
  test.each([
    [1, '1_49'],
    [49, '1_49'],
    [50, '50_99'],
    [999, '200_999'],
    [1000, '1000_5000'],
    [100_000, '50001_100000'],
    [100_001, 'more_than_100000'],
    [1_000_000, 'more_than_100000'],
  ])('%i creditors -> %s', (count, expected) => {
    expect(creditorsBand(count)).toBe(expected);
  });

  test('zero and negative counts derive nothing — the form has no such box', () => {
    expect(creditorsBand(0)).toBeUndefined();
    expect(creditorsBand(-1)).toBeUndefined();
  });

  test('a non-finite count derives nothing', () => {
    expect(creditorsBand(Number.NaN)).toBeUndefined();
  });
});

describe('dollarBand', () => {
  test.each([
    ['0.00', '0_50000'],
    ['50000.00', '0_50000'],
    // Falls between one bracket's whole-dollar max and the next one's min —
    // rounds down into the bracket it is closest to (see `bandFor`).
    ['50000.01', '0_50000'],
    ['50001.00', '50001_100000'],
    ['1234.05', '0_50000'],
    ['10000000.00', '1000001_10000000'],
    ['50000000001.00', 'more_than_50000000000'],
  ])('$%s -> %s', (amount, expected) => {
    expect(dollarBand(amount)).toBe(expected);
  });

  test('accepts a plain number as well as the string the summary endpoint returns', () => {
    expect(dollarBand(500_000)).toBe('100001_500000');
    expect(dollarBand(500_001)).toBe('500001_1000000');
  });

  test('a negative or unreadable total derives nothing', () => {
    expect(dollarBand('-5.00')).toBeUndefined();
    expect(dollarBand('not a number')).toBeUndefined();
  });
});
