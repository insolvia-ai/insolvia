import {
  colors,
  radii as baseRadii,
  spacing,
  typography as baseTypography,
} from '@insolvia-ai/tokens';
import type { ColorScheme, ColorSchemeName, Typography } from '@insolvia-ai/tokens';

import { brandColors, brandFonts, brandRadii } from './brand-colors';
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
  /**
   * The corner scale. `typeof baseRadii` would be the package's literal zeroes
   * and would reject the brand's own corners — the same reason `typography` is
   * typed by its interface rather than by the const.
   */
  readonly radii: Readonly<Record<keyof typeof baseRadii, number>>;
  /**
   * The type families. `Typography` and not `typeof baseTypography`: the
   * package declares its own `as const`, so that would be the LITERAL system
   * stacks and would reject any brand replacing them — which is the seam.
   */
  readonly typography: BrandTypography;
  readonly fontSizes: typeof fontSizes;
}

/**
 * The three roles `@insolvia-ai/tokens` declares plus Insolvia's fourth.
 *
 * `wordmark` is the logotype's face and nothing else's — see brand/fonts.json
 * for why the UI is one sans and the serif is kept for the mark. The package
 * has no such role and ignores the key when `ThemeProvider` receives it; the
 * type exists so the one component that reads it can do so without a cast.
 */
export type BrandTypography = Typography & { readonly wordmark: string };

/**
 * The type scale, in density-independent pixels.
 *
 * `@insolvia-ai/tokens` carries font *families* but no sizes.
 * This is therefore the single place a font size is declared; a
 * component that spells one out inline is a bug. Promote this to `tokens.json`
 * the moment marketing needs the same numbers.
 *
 * SIX STEPS, AND EACH PAIRS WITH A LINE HEIGHT IN `Heading` OR THE SCREEN
 * THAT USES IT. The steps were sized for a serif display face, which needed
 * 34px to hold its own against 16px body copy; the UI is one sans now
 * (brand/fonts.json), and Open Sans's tall x-height makes 28 read as large
 * as Cormorant's 34 did. The ratios are close to the platform scales this
 * app sits beside — 12 · 14 · 16 · 20 · 28 is within a pixel of Material's
 * label/body/title/headline steps and of macOS's caption/body/title steps —
 * so a control from the design system (whose own scale is 12/14/16/18) sits
 * on the same grid as the text around it.
 */
export const fontSizes = {
  /** 12 — captions, metadata, and tracked uppercase labels. */
  caption: 12,

  /** 14 — table cells, list rows, button and badge labels. */
  label: 14,

  /** 16 — body copy, and the title of a card. */
  body: 16,

  /** 20 — a section heading within a page. */
  section: 20,

  /** 22 — the wordmark in the app header. Cormorant, not Open Sans. */
  wordmark: 22,

  /** 28 — the page's one display heading. */
  display: 28,
} as const;

/**
 * The width the centered content column is capped at — a full-width line of
 * text on a 2560px display is unreadable.
 */
export const contentMaxWidth = 720;

/**
 * The cap for the CONTENT BESIDE a case's rail — not for the workspace itself.
 *
 * It exists because {@link contentMaxWidth} is a *reading* measure, chosen so a
 * line of prose stays short enough to track. Applying it to a creditor matrix
 * is the same number answering a different question: that table carries a name,
 * an address, an account number and an amount, and 720 cannot hold them without
 * wrapping every row into a paragraph.
 *
 * WHAT IT NO LONGER CAPS is the frame. Capping the whole workspace put the rail
 * inside a centred column, which left the app marooned in a strip down the
 * middle of a wide display and the rail floating as an island with the header's
 * rule stopping short of it. The rail is chrome now — flush to the header and
 * the window edge — and this caps only what sits beside it.
 *
 * 1440 rather than something larger because the number still has a job: the
 * spine and the figures put a label at one edge of this measure and its value
 * at the other, and past about this width that pairing stops reading as a row.
 * Letting the content simply fill a 2560px display would put four feet of empty
 * space between "Total liabilities" and its figure.
 */
export const workspaceMaxWidth = 1440;

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
export { spacing } from '@insolvia-ai/tokens';

/**
 * The corner scale, branded.
 *
 * Re-exported from here for the reason `typography` is: importing the
 * package's `radii` directly would be a second, UNBRANDED answer to "how round
 * is this", reachable from any `StyleSheet.create` block. There is one answer.
 */
export const radii = { ...baseRadii, ...brandRadii } as const;

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
    // Brand over base, third seam. The package states 0 at every step because a
    // corner is a brand decision it declines to make; `brand/radii.json` makes
    // it. `pill` is not in the override and falls through, which is correct —
    // the package refuses to theme it.
    radii: { ...baseRadii, ...brandRadii },
    // Brand over base again, and the same layering argument. The package's
    // base theme sets `heading` and `body` to the same system sans and says
    // why — a display face is a brand decision it declines to make — so all
    // four roles here are Insolvia's, from brand/fonts.json (the fourth,
    // `wordmark`, is the app's own). The faces themselves are @font-face'd in
    // public/index.html; this only asks for them, and every stack ends in the
    // generic the base used, so a face that fails to load renders what
    // shipped before it.
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
