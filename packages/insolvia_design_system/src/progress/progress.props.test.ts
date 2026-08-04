// Direct unit tests for the shared percent computation — the part both leaves
// render off of.
import { describe, expect, it } from 'vitest';

import { progressPercent } from './progress.props';

describe('progressPercent', () => {
  it('is 0 for a null value (indeterminate)', () => {
    expect(progressPercent(null, 100)).toBe(0);
  });

  it('is 0 for an undefined value', () => {
    expect(progressPercent(undefined, 100)).toBe(0);
  });

  it('computes the plain percentage within range', () => {
    expect(progressPercent(25, 100)).toBe(25);
    expect(progressPercent(1, 4)).toBe(25);
  });

  it('clamps a value above max to 100', () => {
    expect(progressPercent(150, 100)).toBe(100);
  });

  it('clamps a negative value to 0', () => {
    expect(progressPercent(-10, 100)).toBe(0);
  });

  it('is 0 when max is zero or negative, never a division error', () => {
    expect(progressPercent(50, 0)).toBe(0);
    expect(progressPercent(50, -10)).toBe(0);
  });
});
