import { moneyOf } from './money-input';

describe('moneyOf', () => {
  it('reads an amount the way people write it', () => {
    expect(moneyOf('1200')).toBe(1200);
    expect(moneyOf('$1,200.50')).toBe(1200.5);
    expect(moneyOf(' 0.29 ')).toBe(0.29);
  });

  it('is undefined for a blank box or text that is not an amount', () => {
    expect(moneyOf('')).toBeUndefined();
    expect(moneyOf('$')).toBeUndefined();
    expect(moneyOf('12a')).toBeUndefined();
    expect(moneyOf('1.2.3')).toBeUndefined();
    expect(moneyOf('-5')).toBeUndefined();
  });
});
