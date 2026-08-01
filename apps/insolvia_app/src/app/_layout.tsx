import { Stack } from 'expo-router';

import { SessionProvider } from '@/session';

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
 */
export default function RootLayout() {
  return (
    <SessionProvider>
      <Stack screenOptions={{ headerShown: false }} />
    </SessionProvider>
  );
}
