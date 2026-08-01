// Direct unit tests for the scheme resolver the native leaves share. The
// rendered proof (a dark scheme changing actual pixels) lives in
// button.native.test.tsx / field.native.test.tsx; these pin the resolution
// rule itself — in particular that every unknown input takes the light arm,
// mirroring the app's `themeFor`.
import { describe, expect, it } from 'vitest';

import { colors } from '@insolvia-ai/tokens';

import { nativeColorsFor } from './native-theme';

describe('nativeColorsFor', () => {
  it("resolves 'dark' to the dark scheme", () => {
    expect(nativeColorsFor('dark')).toBe(colors.dark);
  });

  it("resolves 'light' to the light scheme", () => {
    expect(nativeColorsFor('light')).toBe(colors.light);
  });

  it('resolves anything else to light — the safe arm', () => {
    expect(nativeColorsFor(null)).toBe(colors.light);
    expect(nativeColorsFor(undefined)).toBe(colors.light);
    expect(nativeColorsFor('unspecified')).toBe(colors.light);
    expect(nativeColorsFor('no-preference')).toBe(colors.light);
  });
});
