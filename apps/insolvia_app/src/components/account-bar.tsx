import { Button } from '@insolvia-ai/design-system';
import { StyleSheet, Text, View } from 'react-native';

import { useSession } from '@/session';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * Who is signed in, and the way out. Lives in {@link AppShell}'s header, so it
 * is on every screen without any of them asking for it.
 *
 * **The address comes from the ID token, never from `/v1/me`.** The pool uses
 * `username_attributes = ["email"]`, which makes the access token's `username`
 * claim a Cognito-generated UUID with no address in it — so `/v1/me`, which
 * reflects verified access-token claims, has none to return. Rendering that
 * UUID where a user expects their email would be a plausible-looking lie. The
 * ID token is the artifact issued *about* the user and carries the `email`
 * claim the `email`/`profile` scopes request; ADR 0007 settles this.
 *
 * Renders nothing at all when there is no session, which is what keeps the
 * shell usable on the sign-in and callback screens.
 */
export function AccountBar() {
  const { status, user, signOut } = useSession();
  const theme = useTheme();

  if (status !== 'signed-in') {
    return null;
  }

  return (
    <View style={styles.bar}>
      {user?.email === null || user?.email === undefined ? null : (
        <Text
          style={[styles.email, { color: theme.colors.muted, fontFamily: theme.typography.body }]}
        >
          {user.email}
        </Text>
      )}
      {/*
        size="lg" (48dp) for the 44dp WCAG 2.5.5 target-size floor this app
        enforces — the package's `md` is 40dp. No glyph and no aria-label: the
        visible text is the whole accessible name, which is the contract the
        end-to-end suite matches on.
      */}
      <Button size="lg" intent="secondary" onPress={signOut}>
        Sign out
      </Button>
    </View>
  );
}

const styles = StyleSheet.create({
  bar: {
    alignItems: 'center',
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
  },
  email: {
    fontSize: fontSizes.label,
  },
});
