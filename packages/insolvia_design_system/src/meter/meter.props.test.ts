// Direct unit tests for the shared percent computation.
import { describe, expect, it } from 'vitest';

import { meterPercent } from './meter.props';

describe('meterPercent', () => {
  it('computes the plain percentage within a default [0, max] range', () => {
    expect(meterPercent(25, 0, 100)).toBe(25);
  });

  it('accounts for a non-zero min', () => {
    expect(meterPercent(15, 10, 20)).toBe(50);
  });

  it('clamps a value above max to 100', () => {
    expect(meterPercent(150, 0, 100)).toBe(100);
  });

  it('clamps a value below min to 0', () => {
    expect(meterPercent(-5, 0, 100)).toBe(0);
  });

  it('is 0 when the range is empty or inverted, never a division error', () => {
    expect(meterPercent(5, 10, 10)).toBe(0);
    expect(meterPercent(5, 10, 0)).toBe(0);
  });
});
