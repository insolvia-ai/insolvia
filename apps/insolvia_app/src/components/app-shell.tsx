import { permits } from '@insolvia-ai/api-client';
import type { ReactNode } from 'react';
import { useState } from 'react';
import { Link } from 'expo-router';
import type { ExternalPathString } from 'expo-router';
import { Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';

import { useMembership } from '@/api/me';
import { AccountMenu } from '@/components/account-menu';
import { EnvBadge } from '@/components/env-badge';
import { ThemeToggle } from '@/components/theme-toggle';
import { Wordmark } from '@/components/wordmark';
import { appEnvironment, buildStamp, environmentInfo, marketingUrl } from '@/config/environment';
import { contentMaxWidth, fontSizes, spacing, useTheme } from '@/theme';

export interface AppShellProps {
  children: ReactNode;

  /** Overrides the centered column's cap. Ignored when `frame` is `workspace`. */
  maxContentWidth?: number;

  /**
   * How `main` lays its child out.
   *
   * `document` (the default) is the centered, capped, padded column every
   * reading screen wants — an account form, a questionnaire, a list.
   *
   * `workspace` hands the child the whole frame: no cap, no centering, no
   * padding. It is for a screen that carries its own CHROME — today that means
   * {@link CaseShell}, whose navigation rail has to sit flush against the
   * header above it and the window edge beside it. Rendered in a `document`
   * frame the rail became an island floating in the middle of the page, with
   * the header's rule stopping short of it on both sides, and on a wide display
   * the whole app looked marooned in a 1180px strip. Padding a workspace is the
   * child's job, because only the child knows which of its parts is chrome and
   * which is content.
   */
  frame?: 'document' | 'workspace';
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
 * The header ends with {@link AccountMenu} — the signed-in user's name, email
 * and the way out, behind one avatar. It lives here, not on each screen, so
 * signing out is reachable from wherever the user happens to be; it renders
 * `null` when there is no session, which is what keeps this frame usable on
 * `/sign-in` and `/auth/callback`. Because of it, every `AppShell` needs a
 * `SessionProvider` above it — which `src/app/_layout.tsx` guarantees.
 *
 * ## Two things this owns that look like they belong elsewhere
 *
 * **The env badge is rendered here, unconditionally.** It used to arrive
 * through an `actions` prop each screen passed, which meant it vanished on
 * every `StatusScreen` — the loading and error states, which are exactly when
 * "which environment am I on?" is worth answering. It is now the single
 * answer to that question anywhere in the app, which is what let the home
 * screen stop repeating it in prose.
 *
 * **The account menu's open state.** The design system's dropdown cannot
 * dismiss on an outside press — React Native has no document to listen to, and
 * its native leaf says so. What closes it is a full-screen press target, and
 * that target has to be a sibling of the whole page rather than of the menu:
 * inside the header it would be clipped to the header's own box, because
 * react-native-web gives every View `position: relative`. So the state lives
 * here, and {@link AccountMenu} is controlled.
 */
export function AppShell({
  children,
  maxContentWidth = contentMaxWidth,
  frame = 'document',
}: AppShellProps) {
  const theme = useTheme();
  const membership = useMembership();
  const env = environmentInfo(appEnvironment);
  const [menuOpen, setMenuOpen] = useState(false);

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
  const footerLink = [
    styles.footerText,
    { color: theme.colors.muted, fontFamily: theme.typography.body },
  ];

  return (
    <View style={[styles.page, { backgroundColor: theme.colors.bg }]}>
      {/* Below the header in the tree but above `main` in paint order, so a
          press anywhere on the page closes the menu. Hidden from assistive
          tech: it is a mouse affordance, and the menu already closes on Escape
          through the trigger. */}
      {menuOpen ? (
        <Pressable
          accessibilityElementsHidden
          importantForAccessibility="no-hide-descendants"
          onPress={() => {
            setMenuOpen(false);
          }}
          style={styles.dismissLayer}
          // The one `testID` in the shell, and it is unavoidable: this element
          // is deliberately absent from the accessibility tree, so it has no
          // role and no name to be addressed by. Giving it one to make it
          // queryable would announce a control that does nothing for a
          // keyboard user, which is worse than a test-only handle.
          testID="account-menu-dismiss"
        />
      ) : null}

      <View role="banner" style={[styles.header, { borderBottomColor: theme.colors.line }]}>
        <Wordmark />
        <View role="navigation" aria-label="Primary" style={styles.nav}>
          <Link href="/" style={navLink}>
            Home
          </Link>
          {/* Cases belongs in the primary nav because a case is what this
              product is about. It was reachable only through two buttons on
              the home screen, which put the app's central object one hop
              further away than the firm's settings. */}
          <Link href="/cases" style={navLink}>
            Cases
          </Link>
          {showFirmLink ? (
            <Link href="/firm" style={navLink}>
              Firm
            </Link>
          ) : null}
        </View>
        <EnvBadge env={env.name} />
        <ThemeToggle />
        <AccountMenu open={menuOpen} onOpenChange={setMenuOpen} />
      </View>

      <View role="main" style={styles.main}>
        <ScrollView
          contentContainerStyle={
            frame === 'workspace'
              ? styles.workspaceContent
              : [styles.mainContent, { maxWidth: maxContentWidth }]
          }
        >
          {children}
        </ScrollView>
      </View>

      <View role="contentinfo" style={[styles.footer, { borderTopColor: theme.colors.line }]}>
        <View style={styles.footerLinks}>
          {/* expo-router `Link`s rather than the design system's `Footer.Link`:
              that part is a Pressable with `accessibilityRole="link"`, which
              react-native-web renders as `<div role="link">` — no href, so no
              middle-click and no open-in-new-tab. These leave the app, so a
              real anchor matters more than the shared styling.

              THE CASTS ARE LOAD-BEARING, and not a way around a type error.
              `Href` is expo-router's union of this app's OWN routes, generated
              into `.expo/types/router.d.ts` — which only the dev server writes,
              and which CI therefore never has. So `typecheck` passed in CI and
              failed on any machine that had run the app, on two links that were
              always correct: an absolute marketing URL is an external
              destination and can never be a member of that union.
              `ExternalPathString` is the arm expo-router provides to say so. */}
          <Link href={`${marketingUrl(env.name)}/privacy` as ExternalPathString} style={footerLink}>
            Privacy
          </Link>
          <Link href={marketingUrl(env.name) as ExternalPathString} style={footerLink}>
            Get help
          </Link>
        </View>
        {/* THE BUILD STAMP, and the honest home for what the home screen used
            to say in prose. It is what a customer reads back over a call: which
            environment they are on, and exactly which bundle. */}
        <Text
          style={[
            styles.footerText,
            { color: theme.colors.muted, fontFamily: theme.typography.body },
          ]}
        >
          © 2026 Insolvia · {env.label} · {env.host} · {buildStamp}
        </Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  dismissLayer: {
    bottom: 0,
    left: 0,
    position: 'absolute',
    right: 0,
    top: 0,
    // Under the header (which the open menu hangs from) and over everything
    // else. react-native-web makes every View a stacking context, so siblings
    // otherwise paint in document order and `main` would cover this.
    zIndex: 5,
  },
  footer: {
    borderTopWidth: 1,
    gap: spacing.xs,
    paddingHorizontal: spacing.lg,
    paddingVertical: spacing.md,
  },
  footerLinks: {
    flexDirection: 'row',
    gap: spacing.md,
  },
  footerText: {
    fontSize: fontSizes.caption,
  },
  header: {
    alignItems: 'center',
    borderBottomWidth: 1,
    flexDirection: 'row',
    gap: spacing.md,
    // Was `padding: spacing.lg` on all four sides, which with a 48dp button in
    // it made a ~96px header. The account controls are one 44dp avatar now, so
    // the vertical padding can come right down.
    paddingHorizontal: spacing.lg,
    paddingVertical: spacing.sm,
    // Above the dismiss layer, so the open menu is pressable and the layer
    // covers only what is behind it.
    zIndex: 10,
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
  workspaceContent: {
    // No padding, no cap, no centering — the child is chrome and owns all
    // three. `flexGrow` rather than `height: '100%'` so a rail can stretch to
    // the viewport on a short page AND the whole thing still scrolls on a long
    // one.
    flexGrow: 1,
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
