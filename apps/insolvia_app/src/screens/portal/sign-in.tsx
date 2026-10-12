import { Button } from '@insolvia-ai/design-system';
import { useLocalSearchParams, useRouter } from 'expo-router';
import { useEffect } from 'react';
import { StyleSheet, Text } from 'react-native';

import { Heading } from '@/components/heading';
import { PortalShell } from '@/components/portal-shell';
import { StatusScreen } from '@/components/status-screen';
import { safePortalReturnTo, usePortalSession } from '@/session';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * `/portal/sign-in` — where a debtor starts (ADR 0023).
 *
 * Like the staff sign-in, **there is no password form here and there must
 * never be one**: the managed login pages take the credential, through the
 * portal's own app client, and this screen only leaves for them. The copy is
 * written for a member of the public rather than a firm user — the first
 * sign-in follows an invitation email whose temporary password Cognito sent
 * separately, and "choose a new password" is the next thing they will see.
 */
export function PortalSignIn() {
  const theme = useTheme();
  const router = useRouter();
  const { status, isConfigured, error, signIn } = usePortalSession();
  const params = useLocalSearchParams();
  const requestedReturnTo = typeof params.returnTo === 'string' ? params.returnTo : null;
  const returnTo = safePortalReturnTo(requestedReturnTo);

  useEffect(() => {
    if (status === 'signed-in') {
      router.replace(returnTo);
    }
  }, [returnTo, router, status]);

  if (status === 'signed-in') {
    return (
      <StatusScreen
        defer
        shell={PortalShell}
        title="Taking you to your portal"
        message="You are signed in. One moment."
      />
    );
  }

  if (!isConfigured) {
    return (
      <StatusScreen
        tone="error"
        shell={PortalShell}
        title="Sign-in is not configured"
        message={
          'The client portal has no sign-in configured for this environment, so there is ' +
          'nothing to sign in against.'
        }
      />
    );
  }

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  return (
    <PortalShell>
      <Heading level={1}>Sign in to your client portal</Heading>
      <Text style={[styles.body, muted]}>
        Your law firm uses Insolvia to prepare your case. Sign in with the email address your firm
        invited, and the password you chose — or, the first time, the temporary password in the
        email from Insolvia.
      </Text>
      <Text style={[styles.body, muted]}>
        You will be taken to Insolvia&apos;s secure sign-in page, then brought straight back here.
      </Text>
      {/* size="lg" for the 44dp target floor; the visible text is the name
          the e2e flow matches on. */}
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
    </PortalShell>
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
