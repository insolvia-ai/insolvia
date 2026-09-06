import { colors, radii, spacing, typography as baseTypography } from '@insolvia-ai/tokens';

/** WCAG relative-contrast, so a colour pairing can be asserted rather than eyeballed. */
function contrast(a: string, b: string): number {
  const channel = (h: string, i: number) => {
    const c = parseInt(h.slice(i, i + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  const luminance = (hex: string) =>
    0.2126 * channel(hex, 1) + 0.7152 * channel(hex, 3) + 0.0722 * channel(hex, 5);
  // Destructuring a sorted array gives `number | undefined` under
  // noUncheckedIndexedAccess; Math.max/min say the same thing without it.
  const one = luminance(a);
  const two = luminance(b);
  return (Math.max(one, two) + 0.05) / (Math.min(one, two) + 0.05);
}

import { contentMaxWidth, fontSizes, themeFor } from '@/theme';
import { brandColors, brandFonts, brandRadii } from '@/theme/brand-colors';

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

    // `dangerText` is the probe for the OTHER half now. `success` used to be —
    // it fell through to the package — but the warm-neutral palette claims all
    // three status colours, because the base's saturated versions would be the
    // only vivid marks on an otherwise neutral page.
    expect(theme.colors.dangerText).toBe(colors[scheme].dangerText);
  });

  it.each(['light', 'dark'] as const)(
    'leaves dangerText readable on the danger the brand DID claim in %s',
    (scheme) => {
      // The half of layering that can go wrong quietly. The package measures
      // `dangerText` against ITS `danger`; the brand replaced `danger` and did
      // not replace the text colour, so the pairing has to be re-checked rather
      // than assumed. It still clears — 6.63:1 light, 6.76:1 dark.
      const theme = themeFor(scheme);
      expect(contrast(theme.colors.dangerText, theme.colors.danger)).toBeGreaterThanOrEqual(4.5);
    },
  );

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

  it('passes spacing through untouched — the brand states no opinion on it', () => {
    expect(themeFor('dark').spacing).toBe(spacing);
  });

  it.each(['light', 'dark'] as const)('brands the corners, and identically in %s', (scheme) => {
    // Radii USED to pass through unchanged. They no longer do: the base states
    // 0 at every step because a corner is a brand decision, and
    // `brand/radii.json` makes it. Same in both schemes — a corner does not
    // depend on the light level.
    expect(themeFor(scheme).radii.lg).toBe(brandRadii.lg);
    expect(themeFor(scheme).radii.lg).not.toBe(radii.lg);
    expect(themeFor('light').radii).toEqual(themeFor('dark').radii);
  });

  it('leaves pill alone, because the package refuses to theme it', () => {
    // `nativeRadiiWith` drops a `pill` override — the leaves that draw a
    // capsule compute their own — so stating one would be honoured by nothing.
    expect(themeFor('light').radii.pill).toBe(radii.pill);
    expect(brandRadii).not.toHaveProperty('pill');
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

/**
 * The type families.
 *
 * They have their own describe because they reach the screen through TWO seams
 * that nothing else connects: this app's `themeFor`, which its own components
 * read, and `ThemeProvider`'s `fonts`, which the design system's native leaves
 * read. Stating a family in only one renders Insolvia's headings over the
 * package's system-sans buttons and badges, which looks like a half-finished
 * load rather than a bug — see `preference.tsx`.
 */
describe('the brand type families', () => {
  it.each(['light', 'dark'] as const)('states all three families in %s', (scheme) => {
    expect(themeFor(scheme).typography).toEqual(brandFonts);
  });

  it('does not vary the families by scheme', () => {
    // Colours flip; typefaces do not. A brand that shipped two would be a bug
    // nothing else here would catch.
    expect(themeFor('light').typography).toEqual(themeFor('dark').typography);
  });

  it.each(['heading', 'body', 'mono'] as const)('ends the %s stack in a real generic', (role) => {
    // A face that fails to load must still resolve to SOMETHING chosen, rather
    // than to whatever the browser defaults to.
    //
    // This used to assert the stack ended in the generic the BASE used, which
    // was right while every role was a sans. `heading` is a serif now — a
    // deliberate brand decision — so the rule is that the stack names a
    // generic, not that it names the package's.
    const generic = brandFonts[role].slice(brandFonts[role].lastIndexOf(',') + 1).trim();
    expect(['serif', 'sans-serif', 'monospace']).toContain(generic);
  });

  it('gives the heading a serif and the body a sans, not two of a kind', () => {
    // The pairing is the point: the display face carries the brand precisely
    // because it contrasts with the face beside it.
    expect(brandFonts.heading.endsWith('serif')).toBe(true);
    expect(brandFonts.heading.endsWith('sans-serif')).toBe(false);
    expect(brandFonts.body.endsWith('sans-serif')).toBe(true);
  });

  it('names a real family before the fallbacks', () => {
    // Guards the case where a stack is edited down to only generics, which
    // would typecheck, pass every other assertion here, and quietly un-brand
    // the app.
    for (const role of ['heading', 'body', 'mono'] as const) {
      expect(brandFonts[role]).not.toBe(baseTypography[role]);
      expect(brandFonts[role].split(',')[0]?.trim()).not.toMatch(/^(ui-|system-)/);
    }
  });
});
