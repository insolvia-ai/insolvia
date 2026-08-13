/**
 * `@/theme` — the app's whole answer to "what colour is this".
 *
 * A barrel, so the import path every component already uses keeps working
 * while the module behind it is two files: {@link ./theme.ts} for the tokens
 * and the `useTheme()` hook, {@link ./preference.tsx} for the user's chosen
 * colour scheme and the seam that makes the design system honour it.
 *
 * They are separate because the second one imports React and the design
 * system, and `theme.ts` is read by `StyleSheet.create` blocks that want
 * nothing but values.
 */

export {
  contentMaxWidth,
  fontSizes,
  radii,
  spacing,
  themeFor,
  typography,
  useTheme,
} from './theme';
export type { Theme } from './theme';

export { THEME_PREFERENCES, ThemePreferenceProvider, useThemePreference } from './preference';
export type { ThemePreference } from './preference';
