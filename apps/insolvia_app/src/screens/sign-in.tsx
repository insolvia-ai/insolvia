import { Button } from '@insolvia-ai/design-system';
import { useLocalSearchParams, useRouter } from 'expo-router';
import { useEffect } from 'react';
import { StyleSheet, Text } from 'react-native';

import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { StatusScreen } from '@/components/status-screen';
import { safeReturnTo, useSession } from '@/session';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * The sign-in entry point.
 *
 * **There is no password form here, and there must never be one.** Credentials
 * are the hosted UI's job: the app never sees a password, which is the whole
 * point of the authorization-code flow, and is why this screen needs no `Field`
 * at all. The button leaves for Cognito; everything after that happens at
 * `/auth/callback`.
 *
 * Three states, in the order they are handled below:
 *
 * 1. **Already signed in** — bounce onward. Reachable by pressing Back after a
 *    sign-in, or by bookmarking the URL.
 * 2. **No hosted UI configured** — the `local` default, with no
 *    `EXPO_PUBLIC_COGNITO_*` variables. An explicit, announced screen; the
 *    alternative is a button that redirects to `https://undefined/oauth2/…`.
 * 3. **Ready** — the button.
 */
export function SignIn() {
  const theme = useTheme();
  const router = useRouter();
  const { status, isConfigured, error, signIn } = useSession();

  // `useLocalSearchParams` types values as `string | string[]`: a parameter can
  // legally repeat in a query string. `safeReturnTo` rejects the array case
  // along with everything else that is not an in-app path.
  const params = useLocalSearchParams();
  const requestedReturnTo = typeof params.returnTo === 'string' ? params.returnTo : null;

  // The raw value is what gets handed to `signIn` and stored; validation runs at
  // the point of NAVIGATION instead, in `safeReturnTo`, so a `sessionStorage`
  // entry edited between the two is still checked before anything follows it.
  const returnTo = safeReturnTo(requestedReturnTo);

  useEffect(() => {
    if (status === 'signed-in') {
      router.replace(returnTo);
    }
  }, [returnTo, router, status]);

  if (status === 'loading' || status === 'signed-in') {
    return (
      <StatusScreen
        title="Checking your session"
        message="One moment while we confirm you are signed in."
      />
    );
  }

  if (!isConfigured) {
    return (
      <StatusScreen
        tone="error"
        title="Sign-in is not configured"
        message={
          'Sign-in is not configured for this environment. This build has no Cognito hosted UI ' +
          'to send you to, so there is nothing to sign in against.'
        }
      />
    );
  }

  return (
    <AppShell>
      <Heading level={1}>Sign in to Insolvia</Heading>
      <Text style={[styles.body, { color: theme.colors.muted, fontFamily: theme.typography.body }]}>
        You will be taken to Insolvia&apos;s secure sign-in page to enter your credentials, then
        brought straight back here.
      </Text>

      {/*
        size="lg" (48dp) for the 44dp WCAG 2.5.5 floor. No decorative glyph and
        no aria-label: the visible text IS the accessible name, and "Sign in" is
        the name the end-to-end suite matches on.
      */}
      <Button size="lg" onPress={() => void signIn(requestedReturnTo)} style={styles.action}>
        Sign in
      </Button>

      {error === null ? null : (
        <Text
          aria-live="assertive"
          style={[styles.error, { color: theme.colors.ink, fontFamily: theme.typography.body }]}
        >
          {error}
        </Text>
      )}
    </AppShell>
  );
}

const styles = StyleSheet.create({
  action: {
    alignSelf: 'flex-start',
    marginTop: spacing.md,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  error: {
    fontSize: fontSizes.label,
    marginTop: spacing.md,
  },
});
