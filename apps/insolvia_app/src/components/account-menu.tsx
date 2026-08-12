import { Avatar, Dropdown } from '@insolvia-ai/design-system';
import { usePathname, useRouter } from 'expo-router';
import { useEffect } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { useMembership } from '@/api/me';
import { useSession } from '@/session';
import { fontSizes, spacing, useTheme } from '@/theme';

export interface AccountMenuProps {
  readonly open: boolean;
  readonly onOpenChange: (open: boolean) => void;
}

/**
 * Who is signed in, and everything you can do about it, behind one avatar.
 *
 * It replaced a row of three controls — the email as plain text, an "Account"
 * link, and a full-size "Sign out" button — which together were most of the
 * header's height and width for something a user touches rarely.
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
 * so it cannot hold an `Avatar`. `Dropdown.Root` is controllable, so the state
 * lives outside and this supplies its own trigger with the aria wiring the
 * part would have contributed.
 *
 * **The menu is right-aligned by a style override.** `Dropdown.Content` is
 * absolutely positioned at `left: 0` with no alignment prop, which for a
 * trigger at the right edge of the header means a menu running off-screen.
 * `Content` spreads `style` last, so the call site can win.
 *
 * **Dismissal comes from the shell.** The native leaf closes only on an item
 * press or a second trigger press — React Native has no document to listen to,
 * and the package says so. `AppShell` owns the open state and renders the
 * full-screen press target, because that target has to be a sibling of the
 * whole page rather than of this component; and it closes on navigation, since
 * a menu left open over a new screen is worse than either.
 */
export function AccountMenu({ open, onOpenChange }: AccountMenuProps) {
  const { status, user, signOut } = useSession();
  const membership = useMembership();
  const theme = useTheme();
  const router = useRouter();
  const pathname = usePathname();

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
      <Pressable
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
        style={styles.trigger}
      >
        <Avatar.Root size="md">
          <Avatar.Fallback>{initials(fullName, email)}</Avatar.Fallback>
        </Avatar.Root>
      </Pressable>

      <Dropdown.Content
        // See the note above: `left: 0` would run this off the right edge.
        style={{ left: 'auto', right: 0 }}
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
        <Dropdown.Item onSelect={signOut}>Sign out</Dropdown.Item>
      </Dropdown.Content>
    </Dropdown.Root>
  );
}

/**
 * Up to two letters for the avatar.
 *
 * Falls back through name → email → `?` rather than rendering an empty circle:
 * a member whose name is still being asked for (see `RequireProfile`) has a
 * half-populated one, and the header renders before that is resolved.
 */
function initials(fullName: string, email: string | null): string {
  const parts = fullName.split(/\s+/).filter(Boolean);
  if (parts.length >= 2) {
    return `${parts[0]![0]!}${parts[parts.length - 1]![0]!}`.toUpperCase();
  }
  if (parts.length === 1) {
    return parts[0]!.slice(0, 2).toUpperCase();
  }
  const local = email?.trim() ?? '';
  return local === '' ? '?' : local.slice(0, 2).toUpperCase();
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
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
  trigger: {
    alignItems: 'center',
    // The 44dp WCAG 2.5.5 floor this app enforces; the avatar itself is 32.
    height: 44,
    justifyContent: 'center',
    width: 44,
  },
});
