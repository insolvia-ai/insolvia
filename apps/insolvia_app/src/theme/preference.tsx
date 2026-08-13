import { colors } from '@insolvia-ai/tokens';
import type { ColorSchemeName } from '@insolvia-ai/tokens';
import { ThemeProvider } from '@insolvia-ai/design-system';
import type { ReactNode } from 'react';
import { createContext, useCallback, useContext, useMemo, useState } from 'react';
import { useColorScheme } from 'react-native';

import { persistentStore, readFrom, writeTo } from '@/platform/browser';

/**
 * What the user chose, which is not the same as what they get: `system` means
 * "whatever this device says", and resolves to a real scheme at render time.
 *
 * A union rather than an `enum` — `erasableSyntaxOnly` is on, so Metro strips
 * types instead of compiling them and an `enum` would typecheck and then fail
 * at runtime.
 */
export const THEME_PREFERENCES = ['system', 'light', 'dark'] as const;
export type ThemePreference = (typeof THEME_PREFERENCES)[number];

/** The one `localStorage` key this owns. */
const STORAGE_KEY = 'insolvia.theme';

interface ThemePreferenceValue {
  /** What the user chose. `system` is a real answer, not an absence. */
  readonly preference: ThemePreference;
  /** What that resolves to right now, with the OS consulted for `system`. */
  readonly scheme: ColorSchemeName;
  readonly setPreference: (next: ThemePreference) => void;
}

const ThemePreferenceContext = createContext<ThemePreferenceValue | null>(null);

/**
 * Reads the stored preference. Anything unrecognised — a key from a future
 * version, a value somebody edited by hand — is `system`, the same
 * "unknown input takes the safe arm" rule `resolveEnvironment` and `themeFor`
 * both follow.
 */
function storedPreference(): ThemePreference {
  const raw = readFrom(persistentStore(), STORAGE_KEY);
  return THEME_PREFERENCES.includes(raw as ThemePreference) ? (raw as ThemePreference) : 'system';
}

/**
 * The app's colour-scheme preference, and the thing that makes the design
 * system agree with it.
 *
 * ## Why this exists at all, rather than a one-line toggle
 *
 * There is no way to *tell* anything what scheme to use. React Native's
 * `useColorScheme()` reports the device, and on web react-native-web implements
 * it as a `prefers-color-scheme` media query with **no setter** —
 * `Appearance.setColorScheme` does not exist there. The design system's
 * `.native` leaves (which this app renders on every platform) call
 * `useColorScheme()` themselves, deep inside `useNativeColors()`, and nothing
 * in their public API redirects it.
 *
 * So an app-level preference has to be imposed from two sides:
 *
 * 1. **This app's own components** read `useTheme()`, which reads the resolved
 *    scheme from here instead of asking the OS.
 * 2. **The package's components** are handed a `ThemeProvider` whose `light`
 *    and `dark` slots BOTH hold the chosen scheme's palette. Its leaves still
 *    ask the OS which slot to read — and now both answers are the same one.
 *
 * That second half is the trick worth understanding before changing it. It is
 * not a hack around the package so much as the only seam the package offers,
 * used to its limit: overrides are per-scheme, so making the two schemes
 * identical is what makes the OS scheme stop mattering.
 *
 * `colors[scheme]` is a COMPLETE `ColorScheme`, which matters: on native the
 * derived states (`primaryHover`, …) are pre-computed rather than blended at
 * use, so a partial override would move `primary` and leave its hover behind.
 *
 * ## Placement
 *
 * Mounted OUTSIDE `SessionProvider` in `src/app/_layout.tsx` — the sign-in and
 * callback screens are as entitled to the user's chosen scheme as any other,
 * and this depends on no session.
 */
export function ThemePreferenceProvider({ children }: { children: ReactNode }) {
  // Read synchronously in the initialiser, not in an effect. An effect would
  // paint one frame in the OS scheme before correcting itself, which is the
  // flash this preference exists to avoid.
  const [preference, setStored] = useState<ThemePreference>(storedPreference);
  const osScheme = useColorScheme();

  const scheme: ColorSchemeName =
    preference === 'system' ? (osScheme === 'dark' ? 'dark' : 'light') : preference;

  const setPreference = useCallback((next: ThemePreference) => {
    setStored(next);
    // A store that is unavailable or full is not an error here: the choice
    // still holds for the life of the page, which is what `writeTo` promises.
    writeTo(persistentStore(), STORAGE_KEY, next);
  }, []);

  const value = useMemo<ThemePreferenceValue>(
    () => ({ preference, scheme, setPreference }),
    [preference, scheme, setPreference],
  );

  // `system` passes the shared frozen empty object rather than a fresh one, so
  // the package keeps its identity fast-path — `nativeColorsWith` returns the
  // token object itself when there are no overrides, and a new `{}` on every
  // render would defeat every `React.memo` below it.
  const overrides = useMemo(
    () =>
      preference === 'system' ? NO_OVERRIDES : { light: colors[scheme], dark: colors[scheme] },
    [preference, scheme],
  );

  return (
    <ThemePreferenceContext.Provider value={value}>
      <ThemeProvider theme={overrides}>{children}</ThemeProvider>
    </ThemePreferenceContext.Provider>
  );
}

const NO_OVERRIDES = Object.freeze({});

/**
 * The preference and the control over it. For the toggle; components that just
 * need colours use `useTheme()`.
 */
export function useThemePreference(): ThemePreferenceValue {
  const value = useContext(ThemePreferenceContext);
  if (value === null) {
    throw new Error('useThemePreference must be rendered inside <ThemePreferenceProvider>');
  }
  return value;
}

/**
 * The active scheme, falling back to the OS when there is no provider.
 *
 * The fallback is what keeps `useTheme()` usable in a test that renders a
 * component in isolation — without it, every such test would have to mount a
 * provider to render anything at all. It is deliberately the OLD behaviour, so
 * the absent-provider case is "follows the device", not a crash.
 */
export function useColorSchemeName(): ColorSchemeName {
  const value = useContext(ThemePreferenceContext);
  const osScheme = useColorScheme();
  if (value !== null) return value.scheme;
  return osScheme === 'dark' ? 'dark' : 'light';
}
