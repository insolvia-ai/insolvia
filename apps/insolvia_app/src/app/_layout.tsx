import { Stack } from 'expo-router';

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
 */
export default function RootLayout() {
  return <Stack screenOptions={{ headerShown: false }} />;
}
