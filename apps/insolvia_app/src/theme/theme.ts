import { colors, radii, spacing, typography as baseTypography } from '@insolvia-ai/tokens';
import type { ColorScheme, ColorSchemeName, Typography } from '@insolvia-ai/tokens';

import { brandColors, brandFonts } from './brand-colors';
import { useColorSchemeName } from './preference';

/**
 * The app's theme: `@insolvia-ai/tokens` resolved for the active color scheme.
 *
 * There is no styling library here — no Tailwind, no NativeWind, no Unistyles.
 * Components pair a static `StyleSheet.create` block (layout, type, at the
 * bottom of each file) with the colors from this hook, which is the lightest
 * and fastest of the six configurations measured in
 * docs/adr/0004-react-native-replaces-flutter.md.
 *
 * Only the **semantic** color layer is reachable: `@insolvia-ai/tokens` does
 * not export the raw ink/brass/paper palette at all, so UI code cannot couple
 * to it and a re-brand stays a one-file change.
 *
 * That one file is `brand/colors.json` at the repo root. From tokens 0.5.0 the
 * package's base theme is deliberately unbranded — monochrome chrome, square
 * corners, no display face — so Insolvia's navy and brass arrive as overrides
 * layered here rather than as package defaults. `themeFor` does the layering;
 * `brand-colors.ts` next door is generated and must not be edited.
 */
export interface Theme {
  readonly scheme: ColorSchemeName;
  readonly colors: ColorScheme;
  readonly spacing: typeof spacing;
  readonly radii: typeof radii;
  /**
   * The type families. `Typography` and not `typeof baseTypography`: the
   * package declares its own `as const`, so that would be the LITERAL system
   * stacks and would reject any brand replacing them — which is the seam.
   */
  readonly typography: Typography;
  readonly fontSizes: typeof fontSizes;
}

/**
 * The type scale, in density-independent pixels.
 *
 * `@insolvia-ai/tokens` carries font *families* but no sizes.
 * This is therefore the single place a font size is declared; a
 * component that spells one out inline is a bug. Promote this to `tokens.json`
 * the moment marketing needs the same numbers.
 */
export const fontSizes = {
  /** 12 — captions and metadata. */
  caption: 12,

  /** 14 — button and badge labels. */
  label: 14,

  /** 16 — body copy. */
  body: 16,

  /** 22 — the wordmark in the app header. */
  wordmark: 22,

  /** 24 — section headings. */
  section: 24,

  /** 34 — the page's one display heading. */
  display: 34,
} as const;

/**
 * The width the centered content column is capped at — a full-width line of
 * text on a 2560px display is unreadable.
 */
export const contentMaxWidth = 720;

/**
 * The cap for a screen that is a WORKSPACE rather than a document: a case's
 * navigation rail beside its content, and the wide tables that live in there —
 * the creditor matrix, the document list, the extraction queue.
 *
 * It exists because {@link contentMaxWidth} is a *reading* measure, chosen so a
 * line of prose stays short enough to track. Applying it to a creditor matrix
 * is the same number answering a different question: that table carries a name,
 * an address, an account number and an amount, and 720 cannot hold them without
 * wrapping every row into a paragraph. Both screens were capped at 720 because
 * only one number existed.
 *
 * 1180 is a cap, not a width — `AppShell` still centers, so nothing stretches
 * on a very wide display, and {@link CaseShell} stacks the rail above the
 * content below `railBreakpoint` rather than squeezing both.
 */
export const workspaceMaxWidth = 1180;

/**
 * Below this viewport width a case's rail stops sitting beside the content and
 * stacks above it. Measured, not guessed: the rail is 232 and the content needs
 * ~560 before its tables start wrapping, which with the shell's own padding is
 * a little over 880.
 */
export const railBreakpoint = 900;

/**
 * The scheme-independent tokens, re-exported for `StyleSheet.create` blocks.
 *
 * A `StyleSheet.create` block runs once at module load, outside any component,
 * so it cannot call `useTheme()`. Spacing, radii and type do not vary by color
 * scheme, so a static block can read them directly — only *colors* have to be
 * applied from the hook at render time. Importing them from here rather than
 * from `@insolvia-ai/tokens` keeps `@/theme` the single import a component
 * needs.
 */
export { radii, spacing } from '@insolvia-ai/tokens';

/**
 * The type families, branded.
 *
 * Re-exported from here rather than from `@insolvia-ai/tokens` for the reason
 * the whole barrel exists: importing the package's `typography` directly would
 * be a second, UNBRANDED answer to "what font is this", available to any
 * `StyleSheet.create` block that reached for it. There is one answer, and it
 * has Insolvia's faces in it.
 *
 * Safe in a static `StyleSheet.create` block: families do not vary by colour
 * scheme, so unlike colours they need no hook.
 */
export const typography = brandFonts;

/**
 * Builds the theme for a scheme. **Anything but `'dark'` resolves to light** —
 * the same "unknown input takes the safe arm" shape as
 * `resolveEnvironment`, and the reason the parameter is a plain `string`:
 * React Native's own `useColorScheme()` can return `'unspecified'` as well as
 * `null`, and a caller should not have to know that.
 */
export function themeFor(scheme: string | null | undefined): Theme {
  const resolved: ColorSchemeName = scheme === 'dark' ? 'dark' : 'light';
  return {
    scheme: resolved,
    // Brand over base, not brand instead of base. `brandColors` names only the
    // roles Insolvia moves, so every role it does not claim — the status
    // colours, `dangerText`, the overlay values, the neutral ramp — stays the
    // package's, and a tokens release that adds or re-measures one reaches this
    // app without an edit here.
    colors: { ...colors[resolved], ...brandColors[resolved] },
    spacing,
    radii,
    // Brand over base again, and the same layering argument. The package's
    // base theme sets `heading` and `body` to the same system sans and says
    // why — a display face is a brand decision it declines to make — so all
    // three roles here are Insolvia's, from brand/fonts.json. The faces
    // themselves are @font-face'd in public/index.html; this only asks for
    // them, and every stack ends in the generic the base used, so a face that
    // fails to load renders what shipped before it.
    typography: { ...baseTypography, ...brandFonts },
    fontSizes,
  };
}

/**
 * The theme for the active color scheme.
 *
 * The scheme comes from {@link useColorSchemeName}, which resolves the user's
 * own preference and falls back to the OS — `prefers-color-scheme` in the
 * browser — when they have expressed none. Every component in this app reads
 * colours through here, so that one hook is the whole app's answer to "light
 * or dark".
 *
 * The design system's leaves ask the OS directly and cannot be redirected;
 * `ThemePreferenceProvider` is what makes them agree with this. See it.
 */
export function useTheme(): Theme {
  return themeFor(useColorSchemeName());
}
