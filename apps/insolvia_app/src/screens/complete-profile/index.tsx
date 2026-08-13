import { ApiValidationException } from '@insolvia-ai/api-client';
import type { FirmMembership } from '@insolvia-ai/api-client';
import { Button, Field, Input } from '@insolvia-ai/design-system';
import { useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMeActions } from '@/api/me';
import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * "Tell us your name" — the one screen a member with no usable name can reach.
 *
 * {@link RequireProfile} decides when this renders and owns the reasoning for
 * the state; this file is only what it looks like.
 *
 * ## Three decisions worth keeping
 *
 * **It renders inside {@link AppShell}.** Not a bare view — the shell carries
 * the sign-out control, so somebody who cannot or will not answer has a way
 * out. The nav links it renders all lead to gated screens, which re-render
 * this one; that is intended, and it is why sign-out is the only exit.
 *
 * **Both inputs are PREFILLED from what is already stored.** The common case
 * is a first name that was derived correctly and a surname that could not be —
 * making that person retype a name the screen is already showing them would be
 * rude, and would invite them to change a value that was right.
 *
 * **The save adopts the server's answer rather than navigating.** `PATCH
 * /v1/me` returns the same body `GET /v1/me` does, so handing it to
 * {@link useMeActions} re-renders the guard with a name in place and the user
 * lands on the route they originally asked for — deep link intact, no redirect
 * to invent a destination for.
 */
export function CompleteProfile({ membership }: { membership: FirmMembership }) {
  const theme = useTheme();
  const { call } = useApi();
  const { adopt } = useMeActions();

  const [firstName, setFirstName] = useState(membership.firstName);
  const [lastName, setLastName] = useState(membership.lastName);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [failed, setFailed] = useState(false);
  const [saving, setSaving] = useState(false);

  const submit = async () => {
    setSaving(true);
    setFieldErrors({});
    setFailed(false);
    try {
      const result = await call((client) => client.updateMe({ firstName, lastName }));
      if (result.ok) {
        // The guard re-reads this and falls through to the screen the user
        // actually asked for. Nothing navigates.
        adopt(result.value);
      }
      // !ok means the session ended and useApi already navigated.
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        // The server owns validation (ADR 0001); its per-field messages render
        // as-is, under the half they belong to.
        setFieldErrors(cause.fields);
      } else {
        setFailed(true);
      }
    } finally {
      setSaving(false);
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  return (
    <AppShell maxContentWidth={520}>
      {/* The screen's one h1. `page-has-heading-one` is a required axe check
          in app-pr.yml, and this screen replaces whichever one the guarded
          screen would have rendered. */}
      <Heading level={1}>Tell us your name</Heading>

      <Text style={[styles.body, muted]}>
        Your name appears on your firm’s directory, on every case you open, and anywhere you are
        assigned. We only have part of it.
      </Text>

      <View style={styles.form}>
        <Field.Root name="firstName" invalid={Boolean(fieldErrors.firstName)}>
          <Field.Label>First name</Field.Label>
          <Input value={firstName} onValueChange={setFirstName} autoCorrect={false} />
          {fieldErrors.firstName ? <Field.Error match>{fieldErrors.firstName}</Field.Error> : null}
        </Field.Root>

        <Field.Root name="lastName" invalid={Boolean(fieldErrors.lastName)}>
          <Field.Label>Last name</Field.Label>
          <Input value={lastName} onValueChange={setLastName} autoCorrect={false} />
          {fieldErrors.lastName ? <Field.Error match>{fieldErrors.lastName}</Field.Error> : null}
        </Field.Root>

        <View style={styles.actions}>
          <Button size="lg" onPress={submit} disabled={saving}>
            {saving ? 'Saving…' : 'Continue'}
          </Button>
        </View>

        {failed ? (
          <Text
            aria-live="assertive"
            style={[
              styles.notice,
              { color: theme.colors.danger, fontFamily: theme.typography.body },
            ]}
          >
            Could not save your name. Please try again.
          </Text>
        ) : null}
      </View>
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
    marginTop: spacing.md,
  },
  notice: {
    fontSize: fontSizes.label,
  },
});
