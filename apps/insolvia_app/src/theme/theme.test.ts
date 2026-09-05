import { colors, radii, spacing } from '@insolvia-ai/tokens';

import { contentMaxWidth, fontSizes, themeFor } from '@/theme';
import { brandColors } from '@/theme/brand-colors';

/**
 * Theme wiring.
 *
 * Both schemes are asserted rather than just the default one: a dark theme that
 * is declared but never actually reachable looks identical to a correct one until
 * someone opens the app in dark mode.
 */
describe('themeFor', () => {
  it('paints the light canvas in Insolvia’s light canvas', () => {
    expect(themeFor('light').colors.bg).toBe(brandColors.light.bg);
  });

  it('paints the dark canvas in Insolvia’s dark canvas', () => {
    expect(themeFor('dark').colors.bg).toBe(brandColors.dark.bg);
  });

  /**
   * The brand LAYERS over the tokens base rather than replacing it, and both
   * halves of that are load-bearing.
   *
   * From tokens 0.5.0 the package's base theme is deliberately unbranded, so a
   * role Insolvia claims must come from `brand/colors.json` — otherwise the app
   * renders the package's monochrome chrome. But a role it does NOT claim must
   * still come from the package, or every future tokens release (a new role, a
   * re-measured contrast) would be silently pinned to whatever was current when
   * the brand was written.
   *
   * `bg` is claimed and `success` is not, which is exactly why they are the two
   * probed here — see brand/colors.json for why the status colours stay the
   * package's.
   */
  it.each(['light', 'dark'] as const)('layers the brand over the tokens base in %s', (scheme) => {
    const theme = themeFor(scheme);

    expect(theme.colors.bg).toBe(brandColors[scheme].bg);
    expect(theme.colors.bg).not.toBe(colors[scheme].bg);

    expect(theme.colors.success).toBe(colors[scheme].success);
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
    // The cap itself is the property that matters — asserting the rendered
    // width at a desktop resolution would only re-measure it.
    expect(contentMaxWidth).toBeLessThan(1200);
  });
});
