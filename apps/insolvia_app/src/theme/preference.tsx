import type { ColorSchemeName } from '@insolvia-ai/tokens';
import { ThemeProvider } from '@insolvia-ai/design-system';
import type { ReactNode } from 'react';
import { createContext, useCallback, useContext, useMemo, useState } from 'react';
import { useColorScheme } from 'react-native';

import { persistentStore, readFrom, writeTo } from '@/platform/browser';

import { brandColors } from './brand-colors';

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
 * `brandColors[scheme]` carries the derived states (`primaryHover`, …)
 * alongside their base roles, which matters: on native those are pre-computed
 * rather than blended at use, so an override that moved `primary` without them
 * would leave its hover behind. It is otherwise PARTIAL on purpose — every
 * role Insolvia does not claim falls through to the package's own default,
 * which is what keeps a tokens release able to improve them.
 *
 * ## Why there is no longer a no-override case
 *
 * There used to be one: `system` passed a frozen `{}`, letting the package's
 * leaves use their own defaults. That was correct while those defaults WERE
 * Insolvia's — the app and the package shipped the same palette. From tokens
 * 0.5.0 they do not: the base theme is deliberately unbranded, so passing
 * nothing now renders the design system's monochrome chrome next to this app's
 * navy. The brand is therefore supplied in every case, and `system` differs
 * only in letting the two slots hold DIFFERENT schemes so the OS still decides.
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

  // `system` hands each slot its own scheme and lets the package's leaves ask
  // the OS, exactly as they would unthemed. An explicit preference puts the
  // CHOSEN scheme in BOTH slots — the leaves still ask the OS, and now both
  // answers are the same one, which is what makes the OS scheme stop mattering.
  //
  // Both arms are memoised on the two inputs that decide them, so the object
  // identity is stable across renders. That still matters for the package's
  // `React.memo` boundaries even though neither arm can be the old frozen-empty
  // fast path any more.
  const overrides = useMemo(
    () =>
      preference === 'system'
        ? BRAND_BY_OS_SCHEME
        : { light: brandColors[scheme], dark: brandColors[scheme] },
    [preference, scheme],
  );

  return (
    <ThemePreferenceContext.Provider value={value}>
      <ThemeProvider theme={overrides}>{children}</ThemeProvider>
    </ThemePreferenceContext.Provider>
  );
}

/**
 * The brand with each slot holding its own scheme — the `system` preference,
 * where the package's leaves consult the OS and get the right half either way.
 *
 * Hoisted and frozen so the `system` arm is one stable object for the life of
 * the module rather than a fresh literal per render.
 */
const BRAND_BY_OS_SCHEME = Object.freeze({
  light: brandColors.light,
  dark: brandColors.dark,
});

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
