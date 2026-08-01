// NATIVE-side theme resolution — the one piece every .native leaf shares. The
// `.native.` infix puts this file on the platform-leaf side of the package's
// fence: Metro and tsconfig.native.json's moduleSuffixes resolve it, the web
// typecheck and marketing's Vite never can, and eslint.config.js exempts
// exactly this pattern from the renderer ban that fences the SHARED lib
// modules — so importing react-native here is the leaf privilege, not a hole.
//
// Why a hook at all: a StyleSheet.create block runs once at module load, so a
// color baked into it (or into a module-level `colors.light`) can never follow
// the OS scheme — that was the 0.2.1 dark-mode regression. Leaves keep layout
// static in StyleSheet.create and apply colors from this hook at render time,
// the same split as apps/insolvia_app/src/theme.ts.
import { useColorScheme } from 'react-native';

import { colors, type ColorScheme } from '@insolvia-ai/tokens';

/**
 * The semantic colors for a scheme name. **Anything but `'dark'` resolves to
 * light** — mirroring the app's `themeFor`, and for the same reason the
 * parameter is a plain `string`: React Native's `useColorScheme()` can return
 * `'unspecified'` as well as `null`, and an unknown input takes the safe arm.
 */
export function nativeColorsFor(scheme: string | null | undefined): ColorScheme {
  return colors[scheme === 'dark' ? 'dark' : 'light'];
}

/**
 * The semantic colors for the ACTIVE scheme — the OS setting on native,
 * `prefers-color-scheme` on app-web via react-native-web.
 */
export function useNativeColors(): ColorScheme {
  return nativeColorsFor(useColorScheme());
}
