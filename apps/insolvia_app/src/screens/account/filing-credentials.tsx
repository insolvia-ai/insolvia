import { ApiException, ApiValidationException } from '@insolvia-ai/api-client';
import type { FilingCredential } from '@insolvia-ai/api-client';
import { Button, Field, Input, PasswordInput } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

type Notice = { readonly tone: 'saved' | 'error'; readonly message: string };

/**
 * Your CM/ECF login, for Insolvia to file under (ADR 0024, guardrail 3) —
 * enrol it, see that it is held, revoke it.
 *
 * Rendered only for a member holding `electronic_filing` (hidden for every
 * role by default; an admin grants it). Everything here is about the
 * SIGNED-IN person's own credential: the API keys every call by the
 * caller's own subject, so there is no way to name anybody else's.
 *
 * THE SECRET GOES ONE WAY. The password and the authenticator key are sent
 * once, sealed by the server on arrival, and never come back — the list
 * shows the login name, the courts and when it was enrolled, and nothing
 * else exists to show. So both fields are cleared the moment the enrolment
 * is accepted, and there is no "show my saved password": changing it is
 * revoke, then enrol again.
 *
 * NO FORM WITHOUT A SIGNED AUTHORIZATION (ADR 0024, guardrail 2): the API
 * refuses an enrolment until the attorney's filing authorization is current,
 * so the form is replaced by a pointer to it until then (`authorized`, from
 * `CourtFiling`).
 */
export function FilingCredentials({
  canChange,
  authorized,
}: {
  readonly canChange: boolean;
  readonly authorized: boolean;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const [credentials, setCredentials] = useState<readonly FilingCredential[] | null>(null);
  const [login, setLogin] = useState('');
  const [password, setPassword] = useState('');
  const [seed, setSeed] = useState('');
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [notice, setNotice] = useState<Notice | null>(null);
  const [busy, setBusy] = useState(false);
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.listFilingCredentials());
      if (result.ok) setCredentials(result.value);
    } catch {
      setNotice({ tone: 'error', message: 'Could not load your filing login.' });
    }
  }, [call]);

  useEffect(() => {
    void load();
  }, [load]);

  const enrol = async () => {
    setBusy(true);
    setFieldErrors({});
    setNotice(null);
    try {
      const result = await call((client) =>
        client.enrolFilingCredential({ login, password, totp_seed: seed }),
      );
      if (result.ok) {
        // The secret's last moment on this device: clear it now, not later.
        setPassword('');
        setSeed('');
        setLogin('');
        setCredentials((current) => [...(current ?? []), result.value]);
        setNotice({ tone: 'saved', message: 'Your filing login is sealed and stored.' });
      }
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setFieldErrors(cause.fields);
      } else if (cause instanceof ApiException && cause.statusCode === 409) {
        setNotice({ tone: 'error', message: cause.message });
      } else {
        setNotice({
          tone: 'error',
          message: 'Could not store your filing login. Please try again.',
        });
      }
    } finally {
      setBusy(false);
    }
  };

  const revoke = async (credential: FilingCredential) => {
    setBusy(true);
    setNotice(null);
    try {
      const result = await call((client) => client.revokeFilingCredential(credential.id));
      if (result.ok) {
        setCredentials((current) => (current ?? []).filter((c) => c.id !== credential.id));
        setNotice({
          tone: 'saved',
          message: `${credential.login} is revoked. Insolvia can no longer file with it.`,
        });
      }
    } catch {
      setNotice({
        tone: 'error',
        message: 'Could not revoke your filing login. Please try again.',
      });
    } finally {
      setBusy(false);
    }
  };

  return (
    <View style={styles.section}>
      <Heading level={2}>Court filing login</Heading>
      <Text style={[styles.body, muted]}>
        Your PACER login and authenticator key, for Insolvia to file under your CM/ECF account once
        you approve each filing. They are sealed when you save them; nobody at your firm or at
        Insolvia can read them back, and revoking destroys them.
      </Text>

      {credentials === null
        ? null
        : credentials.map((credential) => (
            <View key={credential.id} style={styles.row}>
              <Text
                style={[
                  styles.body,
                  { color: theme.colors.ink, fontFamily: theme.typography.body },
                ]}
              >
                {credential.login}
                {credential.courts.length > 0 ? ` — ${credential.courts.join(', ')}` : ''}
              </Text>
              <Text
                style={[styles.caption, muted]}
              >{`Enrolled ${credential.created_at.slice(0, 10)}`}</Text>
              {canChange ? (
                <View style={styles.actions}>
                  <Button
                    intent="secondary"
                    onPress={() => void revoke(credential)}
                    disabled={busy}
                    aria-label={`Revoke ${credential.login}`}
                  >
                    Revoke
                  </Button>
                </View>
              ) : null}
            </View>
          ))}

      {canChange && !authorized ? (
        <Text style={[styles.body, muted]}>
          Sign the filing authorization above before storing your court login.
        </Text>
      ) : null}

      {canChange && authorized ? (
        <>
          <Field.Root name="login" invalid={Boolean(fieldErrors.login)}>
            <Field.Label>PACER username</Field.Label>
            <Input
              value={login}
              onValueChange={setLogin}
              autoCorrect={false}
              autoCapitalize="none"
            />
            {fieldErrors.login ? <Field.Error match>{fieldErrors.login}</Field.Error> : null}
          </Field.Root>
          <Field.Root name="password" invalid={Boolean(fieldErrors.password)}>
            <Field.Label>PACER password</Field.Label>
            <PasswordInput value={password} onValueChange={setPassword} />
            {fieldErrors.password ? <Field.Error match>{fieldErrors.password}</Field.Error> : null}
          </Field.Root>
          <Field.Root name="totp_seed" invalid={Boolean(fieldErrors.totp_seed)}>
            <Field.Label>Authenticator key</Field.Label>
            <PasswordInput
              value={seed}
              onValueChange={setSeed}
              autoComplete="new-password"
              showLabel="Show key"
              hideLabel="Hide key"
            />
            <Field.Description>
              The setup key PACER showed when you turned on multi-factor authentication — the text
              version of its QR code.
            </Field.Description>
            {fieldErrors.totp_seed ? (
              <Field.Error match>{fieldErrors.totp_seed}</Field.Error>
            ) : null}
          </Field.Root>
          <View style={styles.actions}>
            <Button size="lg" onPress={() => void enrol()} disabled={busy}>
              {busy ? 'Saving…' : 'Save filing login'}
            </Button>
          </View>
        </>
      ) : null}

      {notice === null ? null : (
        <Text
          aria-live="polite"
          style={[
            styles.body,
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
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  caption: {
    fontSize: fontSizes.caption,
    lineHeight: fontSizes.caption * 1.5,
  },
  row: {
    gap: spacing.xs,
  },
  section: {
    gap: spacing.md,
    marginTop: spacing.lg,
  },
});
