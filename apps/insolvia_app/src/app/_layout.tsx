import { Stack } from 'expo-router';

import { MeProvider } from '@/api/me';
import { SessionProvider } from '@/session';
import { ThemePreferenceProvider } from '@/theme';

/**
 * The root layout — the only navigator in the app.
 *
 * `headerShown: false` because the shell's own chrome is the header: every
 * screen renders inside `AppShell`, which owns the `<header>`/`<nav>`/`<main>`/
 * `<footer>` landmarks. A native stack header on top of it would duplicate the
 * wordmark and, on web, add a second banner.
 *
 * The frame is deliberately NOT applied here. Wrapping `<Stack>` in `AppShell`
 * would put the landmarks outside the navigator, so a screen could never opt out
 * (a future full-bleed sign-in page, for instance) — and on native it would draw
 * the header above the navigation container rather than inside the screen. Each
 * screen composes `AppShell` itself, which is one line and stays honest.
 *
 * `SessionProvider`, by contrast, DOES wrap the navigator, and has to: the
 * session is mounted exactly once for the app's lifetime. Inside the navigator
 * it would remount on navigation, throwing away the in-memory access token and
 * re-running the refresh-token exchange on every route change. It sits outside
 * `<Stack>` so `/sign-in` and `/auth/callback` — which are not signed-in screens
 * but very much need the session — are inside it too.
 *
 * `MeProvider` wraps the navigator for the same reason the session does: it
 * holds one `/v1/me` answer for the session's lifetime — the shell's
 * navigation and the account screen both read it. Inside the navigator it
 * would refetch on every route change, which is exactly the cost it exists to
 * avoid.
 *
 * `ThemePreferenceProvider` is OUTERMOST, and outside the session on purpose:
 * the sign-in and callback screens are as entitled to the user's chosen colour
 * scheme as any other, and it depends on no session. It also supplies the
 * design system's `ThemeProvider` — see that file for why an app-level scheme
 * choice has to be imposed from two sides.
 */
export default function RootLayout() {
  return (
    <ThemePreferenceProvider>
      <SessionProvider>
        <MeProvider>
          <Stack screenOptions={{ headerShown: false }} />
        </MeProvider>
      </SessionProvider>
    </ThemePreferenceProvider>
  );
}
