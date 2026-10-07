import { ApiException, ApiReauthenticationRequiredException } from '@insolvia-ai/api-client';
import type { FilingAuthorizationStatus } from '@insolvia-ai/api-client';
import { AlertDialog, Button, Checkbox } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { useSession } from '@/session';
import { fontSizes, spacing, useTheme } from '@/theme';

import { FilingCredentials } from './filing-credentials';

type Notice = { readonly tone: 'saved' | 'error'; readonly message: string };

/**
 * How recent a sign-in this screen treats as fresh enough to offer "Sign"
 * directly. The API's window is five minutes
 * (`insolvia_core.filing_authorization.SIGNATURE_MAX_AGE_SECONDS`) and the API
 * is the only judge; this is a minute shorter so a person who pauses on the
 * text is sent to sign in again rather than refused after pressing Sign. If
 * the API refuses anyway, the screen falls back to "sign in again".
 */
const FRESH_SIGN_IN_SECONDS = 240;

/**
 * Court filing on `/account` (ADR 0024): the written authorization
 * (guardrail 2), then the court login it is the precondition for
 * (guardrail 3). One component holds the authorization's state because both
 * halves depend on it — the login form only appears once the authorization is
 * current, and withdrawing it empties the login list.
 */
export function CourtFiling({ canChange }: { readonly canChange: boolean }) {
  const { call } = useApi();
  const [status, setStatus] = useState<FilingAuthorizationStatus | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  // Bumped when a withdrawal destroys the logins, so the list reloads.
  const [credentialsVersion, setCredentialsVersion] = useState(0);

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.getFilingAuthorization());
      if (result.ok) setStatus(result.value);
    } catch {
      setLoadFailed(true);
    }
  }, [call]);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <>
      <FilingAuthorization
        status={status}
        loadFailed={loadFailed}
        canSign={canChange}
        onSigned={setStatus}
        onWithdrawn={() => {
          setCredentialsVersion((n) => n + 1);
          void load();
        }}
      />
      <FilingCredentials
        key={credentialsVersion}
        canChange={canChange}
        authorized={status?.current === true}
      />
    </>
  );
}

/**
 * The authorization itself: read it, sign it after signing in again, see
 * what was signed, withdraw it.
 *
 * SIGNING NEEDS A FRESH SIGN-IN. The API refuses a signature whose sign-in is
 * more than a few minutes old — the session's age does not count, and a
 * refresh does not reset it — so unless the person has just signed in, the
 * button here is "Sign in again to sign", which sends them to the hosted
 * page with `prompt=login` and back to `/account`.
 *
 * THE TEXT RENDERED IS THE TEXT SIGNED: what is posted back is the version and
 * digest this screen was given with the text, and the API refuses any other
 * pair.
 *
 * WITHDRAWING asks once, in a dialog, because it destroys every stored login
 * at once; it needs no fresh sign-in and no edit permission.
 */
function FilingAuthorization({
  status,
  loadFailed,
  canSign,
  onSigned,
  onWithdrawn,
}: {
  readonly status: FilingAuthorizationStatus | null;
  readonly loadFailed: boolean;
  readonly canSign: boolean;
  readonly onSigned: (status: FilingAuthorizationStatus) => void;
  readonly onWithdrawn: () => void;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const { user, signIn } = useSession();
  const [agreed, setAgreed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [needsSignIn, setNeedsSignIn] = useState(false);
  const [confirmWithdraw, setConfirmWithdraw] = useState(false);
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  const authenticatedAt = user?.authenticatedAt ?? null;
  const freshEnough =
    !needsSignIn &&
    authenticatedAt !== null &&
    Date.now() / 1000 - authenticatedAt < FRESH_SIGN_IN_SECONDS;

  const sign = async () => {
    if (status === null) return;
    setBusy(true);
    setNotice(null);
    try {
      const result = await call((client) => client.signFilingAuthorization(status.text));
      if (result.ok) {
        onSigned(result.value);
        setAgreed(false);
        setNotice({ tone: 'saved', message: 'You have signed the filing authorization.' });
      }
    } catch (cause) {
      if (cause instanceof ApiReauthenticationRequiredException) {
        setNeedsSignIn(true);
        setNotice({
          tone: 'error',
          message: 'Your sign-in is no longer recent enough to sign. Sign in again, then sign.',
        });
      } else if (cause instanceof ApiException && cause.statusCode === 409) {
        setNotice({ tone: 'error', message: cause.message });
      } else {
        setNotice({ tone: 'error', message: 'Could not record your signature. Please try again.' });
      }
    } finally {
      setBusy(false);
    }
  };

  const withdraw = async () => {
    setBusy(true);
    setNotice(null);
    try {
      const result = await call((client) => client.withdrawFilingAuthorization());
      if (result.ok) {
        onWithdrawn();
        setNotice({
          tone: 'saved',
          message:
            result.value === 1
              ? 'Withdrawn. Your stored court login was destroyed.'
              : `Withdrawn. ${result.value} stored court logins were destroyed.`,
        });
      }
    } catch {
      setNotice({ tone: 'error', message: 'Could not withdraw. Please try again.' });
    } finally {
      setBusy(false);
    }
  };

  if (status === null) {
    return (
      <View style={styles.section}>
        <Heading level={2}>Filing authorization</Heading>
        {loadFailed ? (
          <Text style={[styles.body, ink, { color: theme.colors.danger }]}>
            Could not load your filing authorization.
          </Text>
        ) : (
          <Text style={[styles.body, muted]}>Loading…</Text>
        )}
      </View>
    );
  }

  const signature = status.signature;
  const summary = status.current
    ? `You signed version ${signature?.text_version ?? ''} on ${signature?.signed_at.slice(0, 10) ?? ''}. Insolvia may file under your court login only after you approve each filing.`
    : signature !== null
      ? `The authorization has changed since you signed version ${signature.text_version}. Until you sign version ${status.current_version}, Insolvia will not store or use your court login.`
      : 'You have not signed it. Insolvia will not store your court login until you do.';

  return (
    <View style={styles.section}>
      <Heading level={2}>Filing authorization</Heading>
      <Text style={[styles.body, muted]}>{summary}</Text>

      {status.current ? null : (
        <View
          style={[
            styles.document,
            { borderColor: theme.colors.line, borderRadius: theme.radii.md },
          ]}
        >
          {status.text.text
            .split(/\n\s*\n/)
            .filter((paragraph) => paragraph.trim() !== '')
            .map((paragraph, index) => (
              <Text key={index} style={[styles.body, ink]}>
                {paragraph.trim()}
              </Text>
            ))}
          <Text style={[styles.caption, muted]}>
            {`Version ${status.text.version} · fingerprint ${status.text.digest.slice(0, 12)}`}
          </Text>
        </View>
      )}

      {canSign && !status.current ? (
        freshEnough ? (
          <>
            <View style={styles.agree}>
              <Checkbox.Root
                aria-label="I have read this authorization and I agree to it"
                checked={agreed}
                onCheckedChange={setAgreed}
              >
                <Checkbox.Indicator>✓</Checkbox.Indicator>
              </Checkbox.Root>
              <Text style={[styles.body, ink, styles.agreeText]}>
                I have read this authorization and I agree to it.
              </Text>
            </View>
            <View style={styles.actions}>
              <Button size="lg" onPress={() => void sign()} disabled={busy || !agreed}>
                {busy ? 'Signing…' : 'Sign authorization'}
              </Button>
            </View>
          </>
        ) : (
          <>
            <Text style={[styles.body, muted]}>
              To sign, sign in again first: Insolvia asks for your password at the moment you sign,
              not only when your session began.
            </Text>
            <View style={styles.actions}>
              <Button
                size="lg"
                onPress={() => void signIn('/account', { reauthenticate: true })}
                disabled={busy}
              >
                Sign in again to sign
              </Button>
            </View>
          </>
        )
      ) : null}

      {signature !== null ? (
        <View style={styles.actions}>
          <Button
            size="lg"
            intent="danger"
            disabled={busy}
            onPress={() => setConfirmWithdraw(true)}
          >
            Withdraw authorization
          </Button>
        </View>
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

      <AlertDialog.Root
        open={confirmWithdraw}
        onOpenChange={(next) => {
          if (!next) setConfirmWithdraw(false);
        }}
      >
        <AlertDialog.Popup>
          <AlertDialog.Title>Withdraw your filing authorization?</AlertDialog.Title>
          <AlertDialog.Description>
            Every court login you have stored with Insolvia is destroyed at once, and Insolvia will
            file nothing further under it. Filings already submitted are not affected. To let
            Insolvia file for you again you will need to sign again and re-enter your PACER login.
          </AlertDialog.Description>
          <View style={styles.actions}>
            <Button
              size="lg"
              intent="danger"
              disabled={busy}
              onPress={() => {
                setConfirmWithdraw(false);
                void withdraw();
              }}
            >
              Withdraw and destroy logins
            </Button>
            <AlertDialog.Close>Cancel</AlertDialog.Close>
          </View>
        </AlertDialog.Popup>
      </AlertDialog.Root>
    </View>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
  },
  agree: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.sm,
  },
  agreeText: {
    flex: 1,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  caption: {
    fontSize: fontSizes.caption,
    lineHeight: fontSizes.caption * 1.5,
  },
  document: {
    borderWidth: StyleSheet.hairlineWidth,
    gap: spacing.sm,
    padding: spacing.md,
  },
  section: {
    gap: spacing.md,
    marginTop: spacing.lg,
  },
});
