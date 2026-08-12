import { colors, radii, spacing, typography } from '@insolvia-ai/tokens';
import type { ColorScheme, ColorSchemeName } from '@insolvia-ai/tokens';

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
 */
export interface Theme {
  readonly scheme: ColorSchemeName;
  readonly colors: ColorScheme;
  readonly spacing: typeof spacing;
  readonly radii: typeof radii;
  readonly typography: typeof typography;
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
 * The scheme-independent tokens, re-exported for `StyleSheet.create` blocks.
 *
 * A `StyleSheet.create` block runs once at module load, outside any component,
 * so it cannot call `useTheme()`. Spacing, radii and type do not vary by color
 * scheme, so a static block can read them directly — only *colors* have to be
 * applied from the hook at render time. Importing them from here rather than
 * from `@insolvia-ai/tokens` keeps `@/theme` the single import a component
 * needs.
 */
export { radii, spacing, typography } from '@insolvia-ai/tokens';

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
    colors: colors[resolved],
    spacing,
    radii,
    typography,
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
