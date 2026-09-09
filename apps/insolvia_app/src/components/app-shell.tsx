import { permits } from '@insolvia-ai/api-client';
import { Badge, Sidebar, ThemeProvider, useSidebar } from '@insolvia-ai/design-system';
import type { BadgeIntent } from '@insolvia-ai/design-system';
import type { ReactNode } from 'react';
import { useState } from 'react';
import { Link, usePathname, useRouter } from 'expo-router';
import type { ExternalPathString } from 'expo-router';
import {
  Image,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  View,
  useWindowDimensions,
} from 'react-native';

import { useMembership } from '@/api/me';
import { AccountMenu } from '@/components/account-menu';
import { Icon } from '@/components/icon';
import type { IconName } from '@/components/icon';
import { Tile } from '@/components/tile';
import { Wordmark } from '@/components/wordmark';
import { appEnvironment, buildStamp, environmentInfo, marketingUrl } from '@/config/environment';
import { persistentStore, readFrom, writeTo } from '@/platform/browser';
import {
  CHROME_THEME,
  chromeColors,
  contentMaxWidth,
  fontSizes,
  railBreakpoint,
  spacing,
  useTheme,
} from '@/theme';

/** One entry in the nav's case group. */
export interface CaseNavItem {
  readonly key: string;
  readonly label: string;
  readonly icon: IconName;
  readonly active: boolean;
  readonly onPress: () => void;
}

/**
 * The case a screen is inside, as the nav shows it: its identity above its
 * sections, and a way back to the list.
 *
 * DATA, NOT ELEMENTS. `CaseShell` renders this shell and knows the case; the
 * shell renders the nav and knows nothing about cases. Handing over a
 * description rather than a subtree keeps every row a `Sidebar.Item` from one
 * place, so the case's sections and the primary links cannot drift apart in
 * how they look or how they announce.
 */
export interface CaseNav {
  readonly title: string;
  /** The chapter and district — omitted when the title already says it. */
  readonly subtitle: string | null;
  readonly status: { readonly label: string; readonly intent: BadgeIntent };
  readonly items: readonly CaseNavItem[];
  readonly onAllCases: () => void;
}

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
   * padding. It is for a screen whose child decides its own measure — today
   * {@link CaseShell}, whose six screens want different ones. Padding a
   * workspace is the child's job.
   */
  frame?: 'document' | 'workspace';

  /** The case the screen is inside, for the nav's case group. */
  caseNav?: CaseNav;
}

/**
 * The page frame every Insolvia screen sits inside: a dark navigation rail
 * down the left, and the page beside it — `main` over a footer, in one
 * scroller.
 *
 * **One nav, on the left, that opens and collapses.** It used to be a top
 * header carrying the wordmark, three links and an avatar, with the case rail
 * as a second dark column underneath it — two pieces of chrome for one
 * navigation, and a header whose right edge kept growing controls. The rail
 * holds all of it now: the wordmark at the top, the primary links, the case's
 * own sections when the screen is inside one, and who is signed in at the
 * bottom, the shape a workspace product's sidebar takes. Collapsed, it is a
 * 64px strip of letter tiles with the same accessible names, and the choice
 * is remembered (`insolvia.nav`) and defaults to collapsed on a narrow
 * window.
 *
 * **The landmarks are the point.** `role="navigation"` (the rail's nav),
 * `role="main"` and `role="contentinfo"` are what react-native-web maps to
 * real `<nav>`, `<main>` and `<footer>` elements, so "skip to main content"
 * and landmark navigation work. Nothing generates them automatically — they
 * exist because this file declares them once, here, for every screen. There
 * is no `banner` any more: the wordmark lives in the nav, and a `<header>`
 * around one word would be a landmark with nothing in it.
 *
 * Note there is no `role="region"`: a `<section>` without an accessible name
 * is invalid ARIA and axe flags it. Use a heading, not a landmark, to open a
 * block.
 *
 * The rail ends with {@link AccountMenu} — the signed-in user's name, email,
 * environment, appearance and the way out, behind one row. It lives here, not
 * on each screen, so signing out is reachable from wherever the user happens
 * to be; it renders `null` when there is no session, which is what keeps this
 * frame usable on `/sign-in` and `/auth/callback`. Because of it, every
 * `AppShell` needs a `SessionProvider` above it — which
 * `src/app/_layout.tsx` guarantees.
 *
 * ## Two things this owns that look like they belong elsewhere
 *
 * **The environment is named in the footer, unconditionally.** It used to be
 * a pill in the header, arriving through an `actions` prop each screen
 * passed, which meant it vanished on every `StatusScreen` — the loading and
 * error states, which are exactly when "which environment am I on?" is worth
 * answering. The footer's build stamp is the answer that is on every page,
 * signed in or not; the account menu states it too.
 *
 * **The account menu's open state.** The design system's dropdown cannot
 * dismiss on an outside press — React Native has no document to listen to,
 * and its native leaf says so. What closes it is a full-screen press target,
 * and that target has to be a sibling of the whole page rather than of the
 * menu: inside the rail it would be clipped to the rail's own box, because
 * react-native-web gives every View `position: relative`. So the state lives
 * here, and {@link AccountMenu} is controlled.
 */
/**
 * THE RAIL AND THE FOOTER ARE ALWAYS DARK, in both colour schemes — they are
 * chrome, not page content, and `@/theme`'s `chromeColors` owns why. The
 * footer closes the document with a band that reads as the edge of the
 * product rather than as more page.
 */
/**
 * THE "I" TILE, served from `public/` rather than imported.
 *
 * It is a GENERATED mark — `brand/icon.svg` cut from the display face, coloured
 * by `npm run tokens`, gated by `npm run tokens:check`. Inlining its path data
 * into this component would fork a generated artifact, which is exactly what
 * "never hand-edit a mark" forbids, and the drift check could not see the copy.
 *
 * Its ground is the dark chrome colour, which is the SAME colour the footer
 * band uses, so the rounded square disappears into the band and what reads is
 * the ivory letterform on black. That is not a happy accident to rely on
 * blindly, but it is stable by construction: both resolve from the dark
 * scheme's roles in `brand/colors.json`, so a brand change moves them together.
 */
const FOOTER_MARK = '/favicon.svg';

/** Where the rail remembers whether it was collapsed. */
const NAV_STORAGE_KEY = 'insolvia.nav';

/**
 * Whether the rail starts collapsed.
 *
 * The user's last choice wins; failing one, a narrow window starts collapsed
 * because 256px of rail on a 900px window leaves the page too little.
 * Read synchronously in the state initialiser, like the theme preference, so
 * the rail does not paint open and then snap shut.
 */
function initialCollapsed(width: number): boolean {
  const stored = readFrom(persistentStore(), NAV_STORAGE_KEY);
  if (stored === 'collapsed') return true;
  if (stored === 'expanded') return false;
  return width < railBreakpoint;
}

export function AppShell({
  children,
  maxContentWidth = contentMaxWidth,
  frame = 'document',
  caseNav,
}: AppShellProps) {
  const theme = useTheme();
  const membership = useMembership();
  const router = useRouter();
  const pathname = usePathname();
  const { width } = useWindowDimensions();
  const env = environmentInfo(appEnvironment);
  const [menuOpen, setMenuOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(() => initialCollapsed(width));

  // A COURTESY, never a control — the same `permits` rule the firm screen
  // documents. The value is MeProvider's session-lifetime read, so a demoted
  // admin may keep the link until they next sign in; the screen's own "an
  // administrator's job" fallback is what actually answers them, and the API
  // enforces regardless.
  const showFirmLink =
    membership != null && permits(membership.permissions.firm_administration, 'view_only');

  const footerLink = [
    styles.footerLinkText,
    { color: chromeColors.ink, fontFamily: theme.typography.body },
  ];

  const primary: ReadonlyArray<{
    label: string;
    icon: IconName;
    href: '/' | '/cases' | '/firm';
    show: boolean;
  }> = [
    { label: 'Home', icon: 'home', href: '/', show: true },
    { label: 'Cases', icon: 'folder', href: '/cases', show: true },
    { label: 'Firm', icon: 'briefcase', href: '/firm', show: showFirmLink },
  ];

  return (
    <View style={[styles.page, { backgroundColor: theme.colors.bg }]}>
      {/* Above `main` in paint order, so a press anywhere on the page closes
          the menu. Hidden from assistive tech: it is a mouse affordance;
          Escape is the menu's own, at document level, with focus handed back
          to the trigger. */}
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

      {/* THE RAIL IS CHROME, dark in both schemes — as dark as the footer,
          which set the colour. The package's leaves in it run under the
          pinned chrome theme, and everything the app draws on it takes
          `chromeColors`. The one exception is the account row's avatar and
          menu, which follow the scheme, and say why. */}
      <ThemeProvider theme={CHROME_THEME}>
        <Sidebar.Root
          collapsed={collapsed}
          onCollapsedChange={(next) => {
            setCollapsed(next);
            writeTo(persistentStore(), NAV_STORAGE_KEY, next ? 'collapsed' : 'expanded');
          }}
          style={[
            styles.rail,
            { backgroundColor: chromeColors.bg, borderRightColor: chromeColors.line },
          ]}
        >
          {/* Expanded, the wordmark leads and the toggle sits at the far edge
              of the same row — a panel glyph, the shape of the thing it
              moves. Collapsed, the head is the mark alone — the "I" in the
              wordmark's serif on an ivory tile, the favicon inverted for the
              dark ground — centred in the strip, with the toggle under it. */}
          {collapsed ? (
            <View style={styles.headCollapsed}>
              {/* Named, because collapsed this IS the wordmark: the same name
                  the expanded head announces, on the tile that stands in. */}
              <View accessible aria-label="Insolvia" role="img">
                <Tile size={32}>I</Tile>
              </View>
              <RailToggle />
            </View>
          ) : (
            <Sidebar.Head>
              <Wordmark onChrome />
              <View style={styles.headToggle}>
                <RailToggle />
              </View>
            </Sidebar.Head>
          )}

          {/* NAMED, and named something other than the package's default:
              `Sidebar.Nav` emits `role="navigation"`, a landmark, and a screen
              reader lists landmarks by name. */}
          <Sidebar.Nav label="Primary">
            {primary.map((entry) =>
              entry.show ? (
                <RailItem
                  key={entry.href}
                  label={entry.label}
                  icon={entry.icon}
                  active={pathname === entry.href}
                  collapsed={collapsed}
                  onPress={() => {
                    router.push(entry.href);
                  }}
                />
              ) : null,
            )}

            {caseNav === undefined ? null : (
              <>
                <Sidebar.Separator />
                {/* The case's identity, above its sections: what the rail is
                    FOR while a case is open. Removed rather than hidden when
                    collapsed, like `Sidebar.Title`, because a truncated name
                    in a 64px strip says nothing a tile does not. */}
                {collapsed ? null : (
                  <View style={styles.caseIdentity}>
                    <Text
                      numberOfLines={2}
                      style={[styles.caseTitle, { fontFamily: theme.typography.heading }]}
                    >
                      {caseNav.title}
                    </Text>
                    {caseNav.subtitle === null ? null : (
                      <Text
                        style={[
                          styles.caseSubtitle,
                          { color: chromeColors.muted, fontFamily: theme.typography.body },
                        ]}
                      >
                        {caseNav.subtitle}
                      </Text>
                    )}
                    <View style={styles.caseStatus}>
                      <Badge intent={caseNav.status.intent} size="sm">
                        {caseNav.status.label}
                      </Badge>
                    </View>
                  </View>
                )}
                {/* A labelled group, not a second landmark: the sections are
                    part of the one navigation, grouped under the case. */}
                <Sidebar.Section title="Case">
                  {caseNav.items.map((item) => (
                    <RailItem
                      key={item.key}
                      label={item.label}
                      icon={item.icon}
                      active={item.active}
                      collapsed={collapsed}
                      onPress={item.onPress}
                    />
                  ))}
                </Sidebar.Section>
                <RailItem
                  label="All cases"
                  icon="arrow-left"
                  active={false}
                  collapsed={collapsed}
                  onPress={caseNav.onAllCases}
                />
              </>
            )}
          </Sidebar.Nav>

          {/* The package's footer pads its own edge and a row inside pads
              again; dropping the footer's pad lands the account row on the
              rail's one left edge, with every item above it. */}
          <Sidebar.Footer style={styles.railFooter}>
            <AccountMenu open={menuOpen} onOpenChange={setMenuOpen} collapsed={collapsed} />
          </Sidebar.Footer>
        </Sidebar.Root>
      </ThemeProvider>

      {/* ONE SCROLLER FOR THE PAGE, holding `main` and the footer as SIBLINGS,
          beside the rail rather than under a header. The footer flows after
          the content and leaves the viewport on a long page, which is what a
          footer is for; the rail stays put, because it carries the way out.

          A `contentinfo` landmark nested inside `main` is invalid ARIA, and
          the landmarks are this component's whole reason for existing — so
          the two are siblings here, both top-level. */}
      <ScrollView style={styles.scrollerFrame} contentContainerStyle={styles.scroller}>
        <View
          role="main"
          style={
            frame === 'workspace'
              ? styles.workspaceContent
              : [styles.mainContent, { maxWidth: maxContentWidth }]
          }
        >
          {children}
        </View>

        <View role="contentinfo" style={styles.footer}>
          {/* Decorative: the build stamp below already names Insolvia in text,
              so announcing the mark too would read the identity twice. */}
          {/* ONE ROW, LEFT-ALIGNED: the mark, then the links beside it, then
              the stamp underneath on the same left edge. The links used to be
              a column pushed to the far right of a 2000px band, opposite the
              mark — two things that belong together, a page-width apart, with
              nothing between them to explain the distance. Two links are not
              a column; they sit where the eye already is. */}
          <View style={styles.footerTop}>
            <Image alt="" source={{ uri: FOOTER_MARK }} style={styles.footerMark} />

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
              <Link
                href={`${marketingUrl(env.name)}/privacy` as ExternalPathString}
                style={footerLink}
              >
                Privacy
              </Link>
              <Link href={marketingUrl(env.name) as ExternalPathString} style={footerLink}>
                Get help
              </Link>
            </View>
          </View>
          {/* THE BUILD STAMP, and the honest home for what the home screen used
            to say in prose. It is what a customer reads back over a call: which
            environment they are on, and exactly which bundle. */}
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

/**
 * The rail's collapse control: a panel glyph — a small outlined rectangle
 * with its left third marked off, the shape of the rail it moves — drawn
 * with Views rather than a character, because no font's glyph for it lines
 * up with the Open Sans beside it and the app ships no icon set.
 *
 * The app's own rather than the package's `Sidebar.Toggle` for the glyph
 * alone; the state, the toggle and the nav id it points `aria-controls` at
 * are the package's, through `useSidebar()`. The accessible name says what
 * pressing does, as the package's does.
 */
function RailToggle() {
  const { collapsed, toggle, navId } = useSidebar();
  const theme = useTheme();
  const webAria = { 'aria-controls': navId } as object;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={collapsed ? 'Expand navigation' : 'Collapse navigation'}
      accessibilityState={{ expanded: !collapsed }}
      aria-expanded={!collapsed}
      {...webAria}
      onPress={toggle}
      style={({ pressed }) => [
        styles.toggle,
        { borderRadius: theme.radii.sm },
        pressed ? { backgroundColor: chromeColors.surfaceAlt } : null,
      ]}
    >
      <View aria-hidden style={[styles.toggleGlyph, { borderColor: chromeColors.muted }]}>
        <View style={[styles.toggleGlyphPane, { borderRightColor: chromeColors.muted }]} />
      </View>
    </Pressable>
  );
}

/**
 * One row of the rail: a line icon and its label.
 *
 * The app's own rather than the package's `Sidebar.Item`, for one reason:
 * collapsed, the package shows the icon alone, and the reference this rail is
 * measured against keeps a small caption under every icon — which is what
 * lets a 64px strip stay legible without hovering. Everything else the
 * package's item does is done the same way here: `accessibilityRole="link"`
 * with the full label as the accessible name in both states (so a collapsed
 * rail is never a column of unnamed links), `aria-current` on the current
 * page, and the 44dp target-size floor.
 */
function RailItem({
  label,
  icon,
  active,
  collapsed,
  onPress,
}: {
  label: string;
  icon: IconName;
  active: boolean;
  collapsed: boolean;
  onPress: () => void;
}) {
  const theme = useTheme();
  // `aria-current` is web-only and outside RN's types; omitted rather than set
  // to undefined when this is not the current page.
  const webAria = (active ? { 'aria-current': 'page' } : {}) as object;
  return (
    <Pressable
      accessibilityRole="link"
      accessibilityLabel={label}
      {...webAria}
      onPress={onPress}
      style={({ pressed }) => [
        collapsed ? styles.itemCollapsed : styles.item,
        { borderRadius: theme.radii.md },
        active || pressed ? { backgroundColor: chromeColors.surfaceAlt } : null,
      ]}
    >
      <Icon
        name={icon}
        size={collapsed ? 20 : 16}
        color={active ? chromeColors.ink : chromeColors.muted}
      />
      <Text
        // Two lines collapsed: the strip is 64px and "Extraction review" is
        // not, and a caption cut to "Extractio…" names nothing.
        numberOfLines={collapsed ? 2 : 1}
        style={[
          collapsed ? styles.itemCaption : styles.itemLabel,
          {
            color: active ? chromeColors.ink : chromeColors.muted,
            fontFamily: theme.typography.body,
          },
        ]}
      >
        {label}
      </Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  caseIdentity: {
    gap: spacing.xs,
    // ONE LEFT EDGE. `Sidebar.Head` pads the title by `md`, a row lands its
    // tile at `sm` margin plus `sm` padding — also `md` — and the separator
    // is inset by `md`.
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
  },
  caseStatus: {
    // `alignItems: 'flex-start'` on the parent would stretch nothing else, but
    // a Badge in a full-width column would grow to fill it.
    flexDirection: 'row',
  },
  caseSubtitle: {
    fontSize: fontSizes.caption,
  },
  caseTitle: {
    color: chromeColors.ink,
    fontSize: fontSizes.body,
    fontWeight: '600',
    lineHeight: fontSizes.body * 1.5,
  },
  dismissLayer: {
    bottom: 0,
    left: 0,
    position: 'absolute',
    right: 0,
    top: 0,
    // Over the page and under the rail (which the open menu hangs from).
    // react-native-web makes every View a stacking context, so siblings
    // otherwise paint in document order and `main` would cover this.
    zIndex: 5,
  },
  footer: {
    backgroundColor: chromeColors.bg,
    gap: spacing.md,
    paddingHorizontal: spacing.lg,
    // Asymmetric on purpose. The rule that used to divide this from the page
    // was doing the work of separation while the footer was pinned; now that it
    // is reached by scrolling to the end, distance says the same thing more
    // quietly, and a hairline across the full width would read as another band
    // of chrome. Generous above, ordinary below.
    paddingBottom: spacing.lg,
    paddingTop: spacing.xl,
  },
  footerTop: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.lg,
  },
  footerLinks: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.lg,
  },
  footerMark: {
    height: 32,
    width: 32,
  },
  footerText: {
    fontSize: fontSizes.caption,
  },
  footerLinkText: {
    fontSize: fontSizes.label,
    // A link is a target: the label rides in a 44dp line box, WCAG 2.5.5's
    // floor, without the row growing a visible button around it.
    lineHeight: 44,
  },
  headCollapsed: {
    alignItems: 'center',
    gap: spacing.xs,
    paddingHorizontal: 0,
  },
  headToggle: {
    marginLeft: 'auto',
  },
  item: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.sm,
    marginHorizontal: spacing.sm,
    // 44dp, the WCAG 2.5.5 target-size floor.
    minHeight: 44,
    paddingHorizontal: spacing.sm,
  },
  itemCaption: {
    fontSize: 11,
    lineHeight: 14,
    maxWidth: 60,
    textAlign: 'center',
  },
  itemCollapsed: {
    alignItems: 'center',
    // Icon over caption with air between rows: the reference this is measured
    // against gives each collapsed entry about a 56px pitch, which is what
    // stops a column of tile-and-word pairs reading as one run of text.
    gap: spacing.xs,
    marginHorizontal: spacing.xs,
    minHeight: 44,
    paddingVertical: spacing.sm,
  },
  itemLabel: {
    flexShrink: 1,
    fontSize: fontSizes.label,
    lineHeight: fontSizes.label * 1.5,
  },
  mainContent: {
    // `flexGrow` so a SHORT page still pushes the footer to the bottom of the
    // viewport rather than leaving it stranded mid-screen with bare canvas
    // under it. On a long page it does nothing and the footer scrolls away.
    flexGrow: 1,
    gap: spacing.md,
    // Centers the column itself; `alignItems` would stretch it instead.
    marginHorizontal: 'auto',
    padding: spacing.xl,
    width: '100%',
  },
  page: {
    flex: 1,
    flexDirection: 'row',
  },
  rail: {
    // Above the dismiss layer, so the open menu is pressable and the layer
    // covers only the page.
    zIndex: 10,
  },
  railFooter: {
    gap: spacing.xs,
    paddingHorizontal: 0,
  },
  scroller: {
    // The page-level scroll container. `flexGrow` rather than a height so its
    // children can share the viewport when the content is short.
    flexGrow: 1,
  },
  scrollerFrame: {
    flex: 1,
    // Without this a long unbroken cell — a filename, an email — makes the
    // flex child refuse to shrink and pushes the rail off screen.
    minWidth: 0,
  },
  toggle: {
    alignItems: 'center',
    // 32 like the package's own toggle: the head row is 32 high, and the
    // glyph is the target's centre rather than its extent.
    height: 32,
    justifyContent: 'center',
    width: 32,
  },
  toggleGlyph: {
    borderRadius: 3,
    borderWidth: 1.5,
    flexDirection: 'row',
    height: 12,
    width: 16,
  },
  toggleGlyphPane: {
    borderRightWidth: 1.5,
    width: 5,
  },
  workspaceContent: {
    // No padding, no cap, no centering — the child owns all three. `flexGrow`
    // rather than `height: '100%'` so the page still scrolls on a long one.
    flexGrow: 1,
  },
});
