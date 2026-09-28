import { Button, ThemeProvider } from '@insolvia-ai/design-system';
import type { ReactNode } from 'react';
import { ScrollView, StyleSheet, Text, View } from 'react-native';

import { Wordmark } from '@/components/wordmark';
import { appEnvironment, buildStamp, environmentInfo } from '@/config/environment';
import { usePortalSession } from '@/session';
import { CHROME_THEME, chromeColors, contentMaxWidth, fontSizes, spacing, useTheme } from '@/theme';

export interface PortalShellProps {
  children: ReactNode;
}

/**
 * The client portal's page frame (ADR 0023) — a band across the top, the page
 * under it, a footer at the end. Deliberately NOT `AppShell`.
 *
 * `AppShell` is the FIRM's frame: its rail links Home, Cases and Firm, its
 * account menu reads the staff session and the staff `/v1/me`, and every one
 * of those is a door a debtor has no business seeing — let alone pressing
 * into a screen whose every call answers 401 to their token. So the portal
 * gets a frame of its own that knows only the portal session: the wordmark,
 * who is signed in, and the way out. Nothing in this file imports the staff
 * session, `MeProvider` or the rail.
 *
 * A TOP BAND, NOT A RAIL, because there is nothing to navigate between yet:
 * one landing screen in this PR, a questionnaire and a document checklist in
 * the next three. A rail for one destination is chrome describing nothing.
 * The band is chrome in the app's sense (`@/theme`'s `chromeColors`: dark in
 * both schemes), so a firm user and their client see the same product, and it
 * stays OUTSIDE the scroller as `AppShell`'s rail does — the way out never
 * scrolls away.
 *
 * Landmarks: `banner` (the band — it holds the wordmark AND the sign-out, so
 * unlike the staff frame it is not a landmark around one word), `main`, and
 * `contentinfo` as `main`'s sibling inside the scroller.
 */
export function PortalShell({ children }: PortalShellProps) {
  const theme = useTheme();
  const { status, user, signOut } = usePortalSession();
  const env = environmentInfo(appEnvironment);

  return (
    <View style={[styles.page, { backgroundColor: theme.colors.bg }]}>
      <View role="banner" style={styles.band}>
        <View style={styles.identity}>
          <Wordmark onChrome />
          <Text
            style={[
              styles.caption,
              { color: chromeColors.muted, fontFamily: theme.typography.body },
            ]}
          >
            Client portal
          </Text>
        </View>
        {status === 'signed-in' ? (
          <View style={styles.account}>
            {user?.email == null ? null : (
              <Text
                numberOfLines={1}
                style={[
                  styles.email,
                  { color: chromeColors.muted, fontFamily: theme.typography.body },
                ]}
              >
                {user.email}
              </Text>
            )}
            {/* On the band, so under the chrome theme: without it a secondary
                button paints near-black ink on the near-black band in light
                mode — the rail's reason for the same nesting. */}
            <ThemeProvider theme={CHROME_THEME}>
              <Button size="lg" intent="secondary" onPress={signOut}>
                Sign out
              </Button>
            </ThemeProvider>
          </View>
        ) : null}
      </View>

      <ScrollView style={styles.scrollerFrame} contentContainerStyle={styles.scroller}>
        <View role="main" style={[styles.main, { maxWidth: contentMaxWidth }]}>
          {children}
        </View>
        <View role="contentinfo" style={styles.footer}>
          <Text
            style={[
              styles.footerText,
              { color: chromeColors.muted, fontFamily: theme.typography.body },
            ]}
          >
            © 2026 Insolvia · {env.label} · {env.host} · {buildStamp}
          </Text>
        </View>
      </ScrollView>
    </View>
  );
}

const styles = StyleSheet.create({
  account: {
    alignItems: 'center',
    flexDirection: 'row',
    flexShrink: 1,
    gap: spacing.md,
    minWidth: 0,
  },
  band: {
    alignItems: 'center',
    backgroundColor: chromeColors.bg,
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
    justifyContent: 'space-between',
    paddingHorizontal: spacing.lg,
    paddingVertical: spacing.sm,
  },
  caption: {
    fontSize: fontSizes.label,
  },
  email: {
    flexShrink: 1,
    fontSize: fontSizes.label,
  },
  footer: {
    backgroundColor: chromeColors.bg,
    paddingBottom: spacing.lg,
    paddingHorizontal: spacing.lg,
    paddingTop: spacing.xl,
  },
  footerText: {
    fontSize: fontSizes.caption,
  },
  identity: {
    alignItems: 'baseline',
    flexDirection: 'row',
    gap: spacing.md,
  },
  main: {
    // Pushes the footer to the viewport's foot on a short page — AppShell's
    // reason for the same rule.
    flexGrow: 1,
    gap: spacing.md,
    marginHorizontal: 'auto',
    padding: spacing.xl,
    width: '100%',
  },
  page: {
    flex: 1,
  },
  scroller: {
    flexGrow: 1,
  },
  scrollerFrame: {
    flex: 1,
    minWidth: 0,
  },
});
