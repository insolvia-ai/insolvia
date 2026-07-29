import { colors, radii, spacing } from '@insolvia-ai/tokens';

import { contentMaxWidth, fontSizes, themeFor } from '@/theme';

/**
 * The port of the Flutter `app_test.dart` theme-wiring test.
 *
 * Both schemes are asserted rather than just the default one: a dark theme that
 * is declared but never actually reachable looks identical to a correct one until
 * someone opens the app in dark mode.
 */
describe('themeFor', () => {
  it('paints the light canvas in light mode', () => {
    expect(themeFor('light').colors.bg).toBe(colors.light.bg);
  });

  it('paints the dark canvas in dark mode', () => {
    expect(themeFor('dark').colors.bg).toBe(colors.dark.bg);
  });

  it('resolves an absent scheme to light rather than throwing', () => {
    // `useColorScheme()` returns null when the platform has no preference.
    expect(themeFor(null).scheme).toBe('light');
    expect(themeFor(undefined).scheme).toBe('light');
  });

  it('gives the two schemes genuinely different colors', () => {
    const light = themeFor('light');
    const dark = themeFor('dark');

    expect(light.colors.bg).not.toBe(dark.colors.bg);
    expect(light.colors.ink).not.toBe(dark.colors.ink);
  });

  it('passes the scheme-independent tokens through unchanged', () => {
    // Spacing and radii are the same in both schemes — only colors differ — so
    // the theme must not fork them.
    expect(themeFor('dark').spacing).toBe(spacing);
    expect(themeFor('dark').radii).toBe(radii);
  });
});

describe('the type scale', () => {
  it('is ordered, so a smaller role never renders larger', () => {
    expect(fontSizes.caption).toBeLessThan(fontSizes.label);
    expect(fontSizes.label).toBeLessThan(fontSizes.body);
    expect(fontSizes.body).toBeLessThan(fontSizes.section);
    expect(fontSizes.section).toBeLessThan(fontSizes.display);
  });
});

describe('the content column', () => {
  it('is capped, so text does not stretch across a desktop-width window', () => {
    // The Flutter equivalent measured the rendered heading at 2560×1440; the cap
    // itself is the property that test was really asserting.
    expect(contentMaxWidth).toBeLessThan(1200);
  });
});
