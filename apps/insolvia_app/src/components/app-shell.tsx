import type { ReactNode } from 'react';
import { Link } from 'expo-router';
import { ScrollView, StyleSheet, Text, View } from 'react-native';

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
 * over a centered, max-width content column on the brand canvas. Ported from the
 * Flutter `AppScaffold`.
 *
 * **The landmarks are the point.** `role="banner"`, `role="navigation"`,
 * `role="main"` and `role="contentinfo"` are what react-native-web maps to real
 * `<header>`, `<nav>`, `<main>` and `<footer>` elements, so "skip to main
 * content" and landmark navigation work. Nothing generates them automatically —
 * they exist because this file declares them once, here, for every screen.
 *
 * Note there is no `role="region"`: a `<section>` without an accessible name is
 * invalid ARIA and axe flags it. Use a heading, not a landmark, to open a block.
 */
export function AppShell({ children, actions, maxContentWidth = contentMaxWidth }: AppShellProps) {
  const theme = useTheme();

  return (
    <View style={[styles.page, { backgroundColor: theme.colors.bg }]}>
      <View role="banner" style={[styles.header, { borderBottomColor: theme.colors.line }]}>
        <Wordmark />
        <View role="navigation" aria-label="Primary" style={styles.nav}>
          <Link
            href="/"
            style={[
              styles.navLink,
              { color: theme.colors.muted, fontFamily: theme.typography.body },
            ]}
          >
            Home
          </Link>
        </View>
        {actions}
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
