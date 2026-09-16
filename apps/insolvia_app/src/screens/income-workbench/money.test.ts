import { fromCents, sumMoney, toCents } from './money';

describe('toCents', () => {
  test('parses a plain decimal string', () => {
    expect(toCents('1200.00')).toBe(120000);
  });

  test('parses a negative amount', () => {
    expect(toCents('-45.50')).toBe(-4550);
  });

  test('pads a one-digit fraction', () => {
    expect(toCents('10.5')).toBe(1050);
  });

  test('treats a whole number with no fraction as exact cents', () => {
    expect(toCents('10')).toBe(1000);
  });

  test('treats an absent value as zero, like a blank box on the form', () => {
    expect(toCents(undefined)).toBe(0);
  });

  test('treats an empty or unparseable string as zero rather than throwing', () => {
    expect(toCents('')).toBe(0);
    expect(toCents('not a number')).toBe(0);
  });

  test('does not reintroduce float imprecision', () => {
    // The classic 0.1 + 0.2 failure, done as decimal strings instead of
    // Number() — this is the whole reason the module exists.
    expect(toCents('0.10') + toCents('0.20')).toBe(30);
  });
});

describe('fromCents', () => {
  test('formats whole dollars with a two-decimal tail', () => {
    expect(fromCents(120000)).toBe('1200.00');
  });

  test('formats a negative amount with the sign in front', () => {
    expect(fromCents(-4550)).toBe('-45.50');
  });

  test('pads a single-digit cents remainder', () => {
    expect(fromCents(1005)).toBe('10.05');
  });

  test('round-trips through toCents', () => {
    for (const value of ['0.00', '1200.00', '-45.50', '9999999.99']) {
      expect(fromCents(toCents(value))).toBe(value);
    }
  });
});

describe('sumMoney', () => {
  test('sums a list of money strings exactly', () => {
    expect(sumMoney(['600.00', '200.00'])).toBe('800.00');
  });

  test('treats undefined entries as zero without dropping the others', () => {
    expect(sumMoney(['100.00', undefined, '50.00'])).toBe('150.00');
  });

  test('an empty list sums to zero', () => {
    expect(sumMoney([])).toBe('0.00');
  });

  test('a long column of cents does not drift the way float addition would', () => {
    const values = Array.from({ length: 1000 }, () => '0.10');
    expect(sumMoney(values)).toBe('100.00');
  });
});
