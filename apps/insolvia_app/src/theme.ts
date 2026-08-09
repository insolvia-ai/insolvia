import { useColorScheme } from 'react-native';
import { colors, radii, spacing, typography } from '@insolvia-ai/tokens';
import type { ColorScheme, ColorSchemeName } from '@insolvia-ai/tokens';

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
 * Dissolves the stacking context react-native-web wraps every `View` in — its
 * base class is `position: relative; z-index: 0`, replicating React Native's
 * layering model on the web.
 *
 * The design system's overlay controls — `Select`'s list, `DateInput`'s
 * picker — elevate themselves and their enclosing `Field.Root` with a z-index
 * while open. A z-index orders an element against its SIBLINGS only, so that
 * elevation stops dead at the first wrapper `View` a screen puts around the
 * control: the wrapper is its own stacking context, and the open list paints
 * — and hit-tests — underneath whatever follows the wrapper. That is the
 * "select renders behind the next section and its options can't be clicked"
 * bug, and it returns every time a screen adds a wrapper, which is why this
 * is a named helper rather than another one-off fix. The design system
 * documents this boundary explicitly (its Field elevates itself, and says a
 * consumer wrapper is the consumer's job — see `field.props.ts` on
 * `controlOpen`).
 *
 * Spread it into the static style of every `View` that sits between an
 * overlay-bearing control and the siblings its overlay must cover. `'auto'`
 * is what a plain `div` has: no stacking context, so the control's elevation
 * carries up to the nearest ancestor that still declares one. The cast is
 * because React Native's types say `number`; react-native-web passes the
 * value through to CSS verbatim.
 *
 * A web-only concern by construction — this app ships web only (ADR 0004). A
 * native client would need overlay layering solved wholesale (there is no CSS
 * `auto` to dissolve into on native; the design system would render the list
 * through a Modal or portal there).
 */
export const noStackingContext = { zIndex: 'auto' as unknown as number } as const;

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
 * `useColorScheme` follows the OS on native and `prefers-color-scheme` in the
 * browser.
 */
export function useTheme(): Theme {
  return themeFor(useColorScheme());
}
