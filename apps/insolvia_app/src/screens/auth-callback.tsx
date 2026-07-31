import { Button } from '@insolvia-ai/design-system';
import { useRouter } from 'expo-router';
import { StyleSheet, Text } from 'react-native';

import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { fontSizes, useTheme } from '@/theme';

/**
 * Placeholder landing screen for the OAuth redirect.
 *
 * The Cognito user pool in `infra/modules/auth` registers `<origin>/auth/callback`
 * as the web client's callback URL, but this build carries **no OIDC client** —
 * sign-in is a separate ticket. This screen exists so that a redirect (or a stale
 * bookmark) lands on branded chrome with a way forward instead of the router's
 * unmatched-route page. It deliberately does not read, validate, or exchange the
 * `code`/`state` query parameters: half an auth flow is worse than none.
 */
export function AuthCallback() {
  const theme = useTheme();
  const router = useRouter();

  return (
    <AppShell>
      <Heading level={1}>Sign-in is not enabled yet</Heading>
      <Text style={[styles.body, { color: theme.colors.muted, fontFamily: theme.typography.body }]}>
        This build has no sign-in flow — accounts land in a later release. Nothing was signed in,
        and nothing was stored.
      </Text>
      {/*
        size="lg" for the 44dp target-size floor; the arrow glyph is aria-hidden
        and aria-label pins the name to the visible label — same reasoning as
        the home screen's CTA.
      */}
      <Button
        size="lg"
        aria-label="Continue to Insolvia"
        onPress={() => {
          // `replace`, not `push`: the callback URL carries no state worth a
          // back-button entry, and returning to it would re-land here.
          router.replace('/');
        }}
      >
        Continue to Insolvia <Text aria-hidden>→</Text>
      </Button>
    </AppShell>
  );
}

const styles = StyleSheet.create({
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
});
