import { Dropdown } from '@insolvia-ai/design-system';
import { usePathname, useRouter } from 'expo-router';
import { useEffect, useRef } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import type { View as ViewHandle } from 'react-native';

import { useMembership } from '@/api/me';
import { appEnvironment, environmentInfo } from '@/config/environment';
import { Tile } from '@/components/tile';
import { onEscapeKey } from '@/platform/browser';
import { useSession } from '@/session';
import { chromeColors, fontSizes, spacing, useTheme, useThemePreference } from '@/theme';
import type { ThemePreference } from '@/theme';

export interface AccountMenuProps {
  readonly open: boolean;
  readonly onOpenChange: (open: boolean) => void;
  /** The rail is collapsed to its 64px strip: the avatar alone, no name. */
  readonly collapsed?: boolean;
}

/**
 * The header's ONE control: who is signed in, where, how the app looks, and
 * everything you can do about any of it, behind one avatar.
 *
 * It replaced a row of three controls — the email as plain text, an "Account"
 * link, and a full-size "Sign out" button — which together were most of the
 * header's height and width for something a user touches rarely. Then it
 * absorbed two more: the environment pill and the light/dark toggle sat
 * beside it as their own boxes, and three unrelated controls in a row read as
 * a toolbar on a header that has nothing to tool. Both are settings-shaped —
 * consulted rarely, changed rarer — which is what a menu behind an avatar is
 * for. The environment is stated in the identity block, because "which
 * deployment am I signed in to" is part of who you are here; the footer's
 * build stamp still says it on every page, signed in or not, so nothing
 * at-a-glance was lost by taking the pill down.
 *
 * THE APPEARANCE ITEMS ARE THREE, NOT A CYCLE. The old toggle cycled
 * system → light → dark because a 44px square could hold one glyph; a menu
 * can hold three named rows, which is the honest shape — a choice, not a
 * step. The current one carries a trailing tick that IS part of its accessible
 * name: `Dropdown.Item` exposes no checked state, so the name is the only
 * channel, and "Light ✓" announced is the state announced. Trailing rather
 * than leading so the three labels stay on one left edge whichever is
 * current — the package's item has no gutter for a mark.
 *
 * **The address still comes from the ID token, never `/v1/me`.** The pool uses
 * `username_attributes = ["email"]`, which makes the access token's `username`
 * a Cognito UUID with no address in it, so `/v1/me` has none to return.
 * Rendering that UUID where a user expects their email would be a
 * plausible-looking lie. ADR 0007 settles this.
 *
 * Renders nothing at all without a session, which is what keeps the shell
 * usable on the sign-in and callback screens.
 *
 * ## Three things the package cannot do here, and what this does instead
 *
 * **The trigger is ours.** `Dropdown.Trigger` wraps its children in a `Text`,
 * so it cannot hold a tile beside a name. `Dropdown.Root` is controllable, so
 * the state lives outside and this supplies its own trigger with the aria
 * wiring the part would have contributed.
 *
 * **The menu opens upward by a style override.** `Dropdown.Content` is
 * absolutely positioned below its trigger with no placement prop, which for
 * the last row of the rail means a panel under the window's bottom edge.
 * `Content` spreads `style` last, so the call site can win.
 *
 * **Dismissal comes from the shell, except Escape.** The native leaf closes
 * only on an item press or a second trigger press — React Native has no
 * document to listen to, and the package says so. `AppShell` owns the open
 * state and renders the full-screen press target, because that target has to
 * be a sibling of the whole page rather than of this component; and it closes
 * on navigation, since a menu left open over a new screen is worse than
 * either. Escape is handled HERE, at document level through
 * `platform/browser.ts`, because closing has to hand focus back to the
 * trigger — the APG menu-button pattern — and the trigger is this component's.
 * Nothing closed on Escape at all before: a comment in the shell said the
 * trigger did, and it did not.
 */
export function AccountMenu({ open, onOpenChange, collapsed = false }: AccountMenuProps) {
  const { status, user, signOut } = useSession();
  const membership = useMembership();
  const theme = useTheme();
  const { preference, setPreference } = useThemePreference();
  const router = useRouter();
  const pathname = usePathname();
  const env = environmentInfo(appEnvironment);
  const trigger = useRef<ViewHandle>(null);

  // Escape closes the menu from wherever focus is, and puts focus back on the
  // trigger so a keyboard user is left where they started rather than nowhere.
  // Subscribed only while open, so a closed menu costs no listener.
  useEffect(() => {
    if (!open) return undefined;
    return onEscapeKey(() => {
      onOpenChange(false);
      trigger.current?.focus();
    });
  }, [open, onOpenChange]);

  // A menu that survived a navigation would hang over a screen the user has
  // already moved on from.
  useEffect(() => {
    onOpenChange(false);
  }, [pathname, onOpenChange]);

  if (status !== 'signed-in') {
    return null;
  }

  const email = user?.email ?? null;
  const fullName = membership?.displayName?.trim() ?? '';

  return (
    <Dropdown.Root open={open} onOpenChange={onOpenChange}>
      {/* THE HEADER IS CHROME; THIS IS NOT. Neither the avatar nor the menu
          takes the chrome's dark palette: the disc follows the scheme like
          every other control (a light disc on the black band in light mode,
          which is what gives it an edge there), and the menu hangs over the
          PAGE, where a dark panel dropping onto a light page would be a second
          colour scheme two inches from the first. */}
      <Pressable
        ref={trigger}
        accessibilityRole="button"
        // A stable name whatever the user is called: the end-to-end suite
        // matches on it, and a label built from a name would change per user.
        aria-label="Account menu"
        aria-haspopup="menu"
        // BOTH FORMS, and the duplication is required — the same rule the
        // design system's own triggers follow. `accessibilityState` is the
        // React Native prop a real device reads; react-native-web does NOT
        // derive `aria-expanded` from it, so the flat form is what reaches the
        // DOM on the web build. Setting only one announces no expanded state
        // on the other platform — WCAG 4.1.2, and invisible to a test that
        // only checks whether the menu mounted.
        accessibilityState={{ expanded: open }}
        aria-expanded={open}
        onPress={() => {
          onOpenChange(!open);
        }}
        style={[styles.trigger, collapsed ? styles.triggerCollapsed : styles.triggerExpanded]}
      >
        {/* The person's initial on the identity tile — the same square the
            collapsed head shows the app's own "I" on — rather than a round
            avatar: one shape for "who", on a rail whose rows are all tiles. */}
        <Tile>{initial(fullName, email)}</Tile>
        {/* ONE LINE: the name, or the address until there is one. The row's
            text sits on the rail, so it takes the chrome's ink; the panel
            carries the address and the environment, so the row need not.
            Removed when collapsed, like every label on the rail — the
            accessible name is the button's. */}
        {collapsed ? null : (
          <Text
            numberOfLines={1}
            style={[styles.name, { color: chromeColors.ink, fontFamily: theme.typography.body }]}
          >
            {fullName === '' ? (email ?? '') : fullName}
          </Text>
        )}
      </Pressable>

      <Dropdown.Content
        // OPENS UPWARD: the trigger is the last thing in the rail, so the
        // package's `top: 100%` would put the panel under the window's bottom
        // edge. `Content` spreads `style` last, so the call site can win.
        style={{ top: 'auto', bottom: '100%', left: 0, marginBottom: spacing.xs }}
      >
        {/* Identity, as a plain block rather than a `Dropdown.Item`. An item is
            a `menuitem` — focusable, activatable — and a name you cannot press
            must not pretend to be one. */}
        <View style={styles.identity}>
          {fullName === '' ? null : (
            <Text
              style={[styles.name, { color: theme.colors.ink, fontFamily: theme.typography.body }]}
            >
              {fullName}
            </Text>
          )}
          {email === null ? null : (
            <Text
              style={[
                styles.email,
                { color: theme.colors.muted, fontFamily: theme.typography.body },
              ]}
            >
              {email}
            </Text>
          )}
          {/* Spelled out rather than the old pill's all-caps abbreviation, so
              the visible text and the announced text are the same string. */}
          <Text
            style={[styles.email, { color: theme.colors.muted, fontFamily: theme.typography.body }]}
          >
            {env.label} environment · {env.host}
          </Text>
        </View>

        <Dropdown.Divider />
        <Dropdown.Item
          onSelect={() => {
            router.push('/account');
          }}
        >
          Your account
        </Dropdown.Item>

        <Dropdown.Divider />
        <Dropdown.Label>Appearance</Dropdown.Label>
        {APPEARANCES.map(({ value, label }) => (
          <Dropdown.Item
            key={value}
            onSelect={() => {
              setPreference(value);
            }}
          >
            {preference === value ? `${label} ✓` : label}
          </Dropdown.Item>
        ))}

        <Dropdown.Divider />
        <Dropdown.Item onSelect={signOut}>Sign out</Dropdown.Item>
      </Dropdown.Content>
    </Dropdown.Root>
  );
}

/**
 * The three colour-scheme preferences, in the order the menu lists them.
 *
 * THREE, NOT TWO, and the first is the point: `system` is what a device that
 * switches to dark in the evening needs, and a plain light/dark pair cannot
 * express it. Its label says what it does rather than naming a mechanism.
 */
const APPEARANCES: ReadonlyArray<{ readonly value: ThemePreference; readonly label: string }> = [
  { value: 'system', label: 'Follow device' },
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
];

/**
 * One letter for the tile.
 *
 * Falls back through name → email → `?` rather than rendering an empty tile:
 * a member whose name is still being asked for (see `RequireProfile`) has a
 * half-populated one, and the rail renders before that is resolved.
 */
function initial(fullName: string, email: string | null): string {
  const name = fullName.trim();
  if (name !== '') return name[0]!.toUpperCase();
  const local = email?.trim() ?? '';
  return local === '' ? '?' : local[0]!.toUpperCase();
}

const styles = StyleSheet.create({
  email: {
    fontSize: fontSizes.caption,
  },
  identity: {
    gap: spacing.xs,
    paddingHorizontal: spacing.sm,
    paddingVertical: spacing.xs,
  },
  name: {
    flexShrink: 1,
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
  trigger: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.sm,
    // The 44dp WCAG 2.5.5 floor this app enforces; the avatar itself is 32.
    // The row lands the avatar on the rail's one left edge: `sm` margin plus
    // `sm` padding, the same sum a `Sidebar.Item` reaches its icon with.
    marginHorizontal: spacing.sm,
    minHeight: 44,
    paddingHorizontal: spacing.sm,
  },
  triggerCollapsed: {
    justifyContent: 'center',
    width: 48,
  },
  triggerExpanded: {
    width: 240,
  },
});
