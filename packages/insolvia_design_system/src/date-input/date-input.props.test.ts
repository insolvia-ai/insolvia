import { describe, expect, it } from 'vitest';

import {
  dateStatus,
  daysInMonth,
  isErrorStatus,
  isRealDate,
  maskDate,
  toIsoDate,
} from './date-input.props';

describe('maskDate', () => {
  it('groups digits as they are typed', () => {
    expect(maskDate('2')).toBe('2');
    expect(maskDate('20190')).toBe('2019-0');
    expect(maskDate('2019-02')).toBe('2019-02');
    expect(maskDate('2019-021')).toBe('2019-02-1');
  });

  it('never emits a trailing separator', () => {
    // This is what keeps Backspace working without any previous-text
    // bookkeeping: the field is never in a state where the last character is a
    // '-' that a delete would remove instead of a digit.
    expect(maskDate('2019')).toBe('2019');
    expect(maskDate('201902')).toBe('2019-02');
  });

  it('accepts a whole date pasted in any punctuation', () => {
    expect(maskDate('2019/02/14')).toBe('2019-02-14');
    expect(maskDate('20190214')).toBe('2019-02-14');
    expect(maskDate('14 Feb 2019')).toBe('1420-19'); // digits in order, nothing clever
  });

  it('ignores letters and stops at eight digits', () => {
    expect(maskDate('abc')).toBe('');
    expect(maskDate('201902144444')).toBe('2019-02-14');
  });

  it('shortens by exactly the digits that were removed', () => {
    expect(maskDate('2019-0')).toBe('2019-0');
    expect(maskDate('2019-')).toBe('2019');
    expect(maskDate('')).toBe('');
  });
});

describe('daysInMonth', () => {
  it('knows the short months', () => {
    expect(daysInMonth(2019, 4)).toBe(30);
    expect(daysInMonth(2019, 1)).toBe(31);
  });

  it('applies the full leap rule, including the century exceptions', () => {
    expect(daysInMonth(2019, 2)).toBe(28);
    expect(daysInMonth(2020, 2)).toBe(29);
    expect(daysInMonth(1900, 2)).toBe(28); // divisible by 100, not by 400
    expect(daysInMonth(2000, 2)).toBe(29); // divisible by 400
  });
});

describe('isRealDate', () => {
  it('rejects dates the calendar does not have', () => {
    expect(isRealDate(2019, 2, 30)).toBe(false);
    expect(isRealDate(2019, 13, 1)).toBe(false);
    expect(isRealDate(2019, 0, 1)).toBe(false);
    expect(isRealDate(2019, 4, 31)).toBe(false);
    expect(isRealDate(2019, 1, 0)).toBe(false);
  });

  it('accepts the awkward real ones', () => {
    expect(isRealDate(2020, 2, 29)).toBe(true);
    expect(isRealDate(2019, 12, 31)).toBe(true);
  });
});

describe('toIsoDate', () => {
  it('returns the ISO date only when eight real digits are present', () => {
    expect(toIsoDate('2019-02-14')).toBe('2019-02-14');
    expect(toIsoDate('2019-02-1')).toBeNull();
    expect(toIsoDate('')).toBeNull();
  });

  it('refuses a date that rolls over rather than silently accepting it', () => {
    // `new Date('2019-02-30')` yields March 2nd without complaint. This is the
    // whole reason the check is arithmetic instead.
    expect(toIsoDate('2019-02-30')).toBeNull();
  });
});

describe('dateStatus', () => {
  it('separates empty from half-typed', () => {
    expect(dateStatus('')).toBe('empty');
    expect(dateStatus('2019-0')).toBe('incomplete');
  });

  it('reports an impossible date as invalid', () => {
    expect(dateStatus('2019-02-30')).toBe('invalid');
  });

  it('applies min and max inclusively', () => {
    expect(dateStatus('2019-02-14', '2019-02-14')).toBe('valid');
    expect(dateStatus('2019-02-13', '2019-02-14')).toBe('out-of-range');
    expect(dateStatus('2019-02-14', undefined, '2019-02-14')).toBe('valid');
    expect(dateStatus('2019-02-15', undefined, '2019-02-14')).toBe('out-of-range');
  });

  it('compares across century and month boundaries as dates, not numbers', () => {
    expect(dateStatus('2019-09-30', undefined, '2019-10-01')).toBe('valid');
    expect(dateStatus('2100-01-01', undefined, '2099-12-31')).toBe('out-of-range');
  });
});

describe('isErrorStatus', () => {
  it('does not call a half-typed date an error', () => {
    // Marking a field red while someone is still typing the year is what
    // teaches people to ignore error styling.
    expect(isErrorStatus('incomplete')).toBe(false);
    expect(isErrorStatus('empty')).toBe(false);
  });

  it('does call an impossible or out-of-range date an error', () => {
    expect(isErrorStatus('invalid')).toBe(true);
    expect(isErrorStatus('out-of-range')).toBe(true);
  });
});
