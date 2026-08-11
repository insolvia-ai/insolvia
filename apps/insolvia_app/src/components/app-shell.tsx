import { permits } from '@insolvia-ai/api-client';
import type { ReactNode } from 'react';
import { Link } from 'expo-router';
import { ScrollView, StyleSheet, Text, View } from 'react-native';

import { useMembership } from '@/api/me';
import { AccountBar } from '@/components/account-bar';
import { Wordmark } from '@/components/wordmark';
import { contentMaxWidth, fontSizes, spacing, useTheme } from '@/theme';

export interface AppShellProps {
  children: ReactNode;

  /** Trailing header content, e.g. an `<EnvBadge />`. */
  actions?: ReactNode;

  /** Overrides the centered column's cap. */
  maxContentWidth?: number;
}

/**
 * The branded page frame every Insolvia screen sits inside: the wordmark header
 * over a centered, max-width content column on the brand canvas.
 *
 * **The landmarks are the point.** `role="banner"`, `role="navigation"`,
 * `role="main"` and `role="contentinfo"` are what react-native-web maps to real
 * `<header>`, `<nav>`, `<main>` and `<footer>` elements, so "skip to main
 * content" and landmark navigation work. Nothing generates them automatically —
 * they exist because this file declares them once, here, for every screen.
 *
 * Note there is no `role="region"`: a `<section>` without an accessible name is
 * invalid ARIA and axe flags it. Use a heading, not a landmark, to open a block.
 *
 * The header ends with {@link AccountBar} — the signed-in user's address and the
 * sign-out control. It lives here, not on each screen, so signing out is
 * reachable from wherever the user happens to be; it renders `null` when there
 * is no session, which is what keeps this frame usable on `/sign-in` and
 * `/auth/callback`. Because of it, every `AppShell` needs a `SessionProvider`
 * above it — which `src/app/_layout.tsx` guarantees for every route.
 */
export function AppShell({ children, actions, maxContentWidth = contentMaxWidth }: AppShellProps) {
  const theme = useTheme();
  const membership = useMembership();

  // A COURTESY, never a control — the same `permits` rule the firm screen
  // documents. The value is MeProvider's session-lifetime read, so a demoted
  // admin may keep the link until they next sign in; the screen's own "an
  // administrator's job" fallback is what actually answers them, and the API
  // enforces regardless.
  const showFirmLink =
    membership != null && permits(membership.permissions.firm_administration, 'view_only');

  const navLink = [
    styles.navLink,
    { color: theme.colors.muted, fontFamily: theme.typography.body },
  ];

  return (
    <View style={[styles.page, { backgroundColor: theme.colors.bg }]}>
      <View role="banner" style={[styles.header, { borderBottomColor: theme.colors.line }]}>
        <Wordmark />
        <View role="navigation" aria-label="Primary" style={styles.nav}>
          <Link href="/" style={navLink}>
            Home
          </Link>
          {showFirmLink ? (
            <Link href="/firm" style={navLink}>
              Firm
            </Link>
          ) : null}
        </View>
        {actions}
        <AccountBar />
      </View>

      <View role="main" style={styles.main}>
        <ScrollView contentContainerStyle={[styles.mainContent, { maxWidth: maxContentWidth }]}>
          {children}
        </ScrollView>
      </View>

      <View role="contentinfo" style={[styles.footer, { borderTopColor: theme.colors.line }]}>
        <Text
          style={[
            styles.footerText,
            { color: theme.colors.muted, fontFamily: theme.typography.body },
          ]}
        >
          © 2026 Insolvia
        </Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  footer: {
    borderTopWidth: 1,
    paddingHorizontal: spacing.lg,
    paddingVertical: spacing.md,
  },
  footerText: {
    fontSize: fontSizes.caption,
  },
  header: {
    alignItems: 'center',
    borderBottomWidth: 1,
    flexDirection: 'row',
    gap: spacing.lg,
    padding: spacing.lg,
  },
  main: {
    flex: 1,
  },
  mainContent: {
    gap: spacing.md,
    // Centers the column itself; `alignItems` would stretch it instead.
    marginHorizontal: 'auto',
    padding: spacing.xl,
    width: '100%',
  },
  nav: {
    flexDirection: 'row',
    flexGrow: 1,
    gap: spacing.md,
  },
  navLink: {
    fontSize: fontSizes.label,
  },
  page: {
    flex: 1,
  },
});
