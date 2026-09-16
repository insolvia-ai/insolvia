import { formatFormDate, statutoryDates } from './statutory-dates';

describe('statutoryDates', () => {
  test('computes the 180-day counseling window start and the 8-year lookback cutoff', () => {
    expect(statutoryDates('2026-06-15')).toEqual({
      counselingWindowStart: '2025-12-17',
      priorCaseLookbackCutoff: '2018-06-15',
    });
  });

  test('crosses a leap day correctly in the 180-day window', () => {
    // 2028 is a leap year; counting 180 days back crosses Feb 29.
    expect(statutoryDates('2028-03-01')).toEqual({
      counselingWindowStart: '2027-09-03',
      priorCaseLookbackCutoff: '2020-03-01',
    });
  });

  test('an expected filing date of Feb 29 lands on another leap year 8 years back', () => {
    // 8 years is always a multiple of 4, so this never has to invent Feb 29
    // in a non-leap year.
    expect(statutoryDates('2028-02-29')).toEqual({
      counselingWindowStart: '2027-09-02',
      priorCaseLookbackCutoff: '2020-02-29',
    });
  });

  test("undefined input yields no dates, not today's arithmetic", () => {
    expect(statutoryDates(undefined)).toBeNull();
  });

  test.each(['', '2026/06/15', '2026-13-01', '2026-02-30', 'not a date'])(
    'rejects a malformed date: %s',
    (value) => {
      expect(statutoryDates(value)).toBeNull();
    },
  );
});

describe('formatFormDate', () => {
  test('renders a stored date as a reading date', () => {
    expect(formatFormDate('2026-06-15')).toBe('June 15, 2026');
  });

  test('falls back to the raw value for a malformed date', () => {
    expect(formatFormDate('not-a-date')).toBe('not-a-date');
  });
});
