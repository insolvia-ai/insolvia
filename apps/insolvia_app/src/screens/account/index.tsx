import { ApiValidationException } from '@insolvia-ai/api-client';
import type { FirmMembership } from '@insolvia-ai/api-client';
import { Button, Field, Input } from '@insolvia-ai/design-system';
import { useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMeActions } from '@/api/me';
import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { EnvBadge } from '@/components/env-badge';
import { Heading } from '@/components/heading';
import { appEnvironment, environmentInfo } from '@/config/environment';
import { useSession } from '@/session';
import { fontSizes, spacing, useTheme } from '@/theme';

type Notice = { readonly tone: 'saved' | 'error'; readonly message: string };

/**
 * Your own account: the name your colleagues see, and the email you sign in
 * with.
 *
 * The name goes to `PATCH /v1/me` — the one thing a member may change about
 * themselves, needing no permission level at all (issue #216). Before this
 * screen existed, a paralegal who mistyped their name at invite time had to
 * ask an administrator to fix it on the firm screen.
 *
 * TWO FIELDS, and either may be saved alone. A row whose halves were derived
 * from a pre-split display name often has a correct first name and an empty
 * surname; this is where that gets fixed, and it is the same endpoint
 * {@link CompleteProfile} writes to.
 *
 * The email is rendered from the ID token (same source as {@link AccountBar},
 * ADR 0007) and is read-only here BY DESIGN, not omission: it is the address
 * Cognito authenticates and sends to, and no endpoint on either side accepts
 * a change to it.
 */
export function Account({ membership }: { membership: FirmMembership }) {
  const theme = useTheme();
  const env = environmentInfo(appEnvironment);
  const { call } = useApi();
  const { user } = useSession();
  const { adopt } = useMeActions();

  const [firstName, setFirstName] = useState(membership.firstName);
  const [lastName, setLastName] = useState(membership.lastName);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [notice, setNotice] = useState<Notice | null>(null);
  const [saving, setSaving] = useState(false);

  const submit = async () => {
    setSaving(true);
    setFieldErrors({});
    setNotice(null);
    try {
      const result = await call((client) => client.updateMe({ firstName, lastName }));
      if (result.ok) {
        // The PATCH answers with the same body as GET /v1/me, so the saved
        // name comes from the server's echo rather than trusting local state.
        setFirstName(result.value.firm?.firstName ?? firstName);
        setLastName(result.value.firm?.lastName ?? lastName);
        // And the shell's cached copy takes the same answer. Without this the
        // header would keep rendering the old initials for the rest of the
        // session, and `RequireProfile` would keep gating on a name that has
        // just been supplied.
        adopt(result.value);
        setNotice({ tone: 'saved', message: 'Your name is saved.' });
      }
      // !ok means the session ended and useApi already navigated.
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        // The server owns validation (ADR 0001); its message renders as-is.
        setFieldErrors(cause.fields);
      } else {
        setNotice({ tone: 'error', message: 'Could not save your name. Please try again.' });
      }
    } finally {
      setSaving(false);
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  return (
    <AppShell actions={<EnvBadge env={env.name} />} maxContentWidth={520}>
      <Heading level={1}>Your account</Heading>

      <View style={styles.form}>
        <Field.Root name="firstName" invalid={Boolean(fieldErrors.firstName)}>
          <Field.Label>First name</Field.Label>
          <Input value={firstName} onValueChange={setFirstName} autoCorrect={false} />
          {fieldErrors.firstName ? <Field.Error match>{fieldErrors.firstName}</Field.Error> : null}
        </Field.Root>

        <Field.Root name="lastName" invalid={Boolean(fieldErrors.lastName)}>
          <Field.Label>Last name</Field.Label>
          <Input value={lastName} onValueChange={setLastName} autoCorrect={false} />
          <Field.Description>
            How your name reads to colleagues — on the firm’s directory, on cases you open, and
            anywhere you are assigned.
          </Field.Description>
          {fieldErrors.lastName ? <Field.Error match>{fieldErrors.lastName}</Field.Error> : null}
        </Field.Root>

        <View style={styles.actions}>
          <Button size="lg" onPress={submit} disabled={saving}>
            {saving ? 'Saving…' : 'Save name'}
          </Button>
        </View>

        {/* One live region for the whole screen, same rule as the firm screen. */}
        {notice === null ? null : (
          <Text
            aria-live="assertive"
            style={[
              styles.notice,
              {
                color: notice.tone === 'error' ? theme.colors.danger : theme.colors.muted,
                fontFamily: theme.typography.body,
              },
            ]}
          >
            {notice.message}
          </Text>
        )}
      </View>

      <Heading level={2}>Sign-in email</Heading>
      {user?.email === null || user?.email === undefined ? null : (
        <Text style={[styles.body, { color: theme.colors.ink, fontFamily: theme.typography.body }]}>
          {user.email}
        </Text>
      )}
      <Text style={[styles.body, muted]}>
        Your email address is your sign-in name and can’t be changed from here.
      </Text>
    </AppShell>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  form: {
    gap: spacing.md,
    marginBottom: spacing.lg,
  },
  notice: {
    fontSize: fontSizes.label,
  },
});
