import { ApiValidationException, permits } from '@insolvia-ai/api-client';
import type { Address, FirmMembership, SignatureBlock } from '@insolvia-ai/api-client';
import { Button, Field, Input } from '@insolvia-ai/design-system';
import { Link } from 'expo-router';
import { useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMeActions } from '@/api/me';
import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { MePanel } from '@/components/me-panel';
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
 * The email is rendered from the ID token (same source as {@link AccountMenu},
 * ADR 0007) and is read-only here BY DESIGN, not omission: it is the address
 * Cognito authenticates and sends to, and no endpoint on either side accepts
 * a change to it.
 */
export function Account({ membership }: { membership: FirmMembership }) {
  const theme = useTheme();
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
    <AppShell maxContentWidth={520}>
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

        <SignatureBlockForm
          initial={membership.signatureBlock}
          onSaved={(principal) => {
            adopt(principal);
            setNotice({ tone: 'saved', message: 'Your signature block is saved.' });
          }}
          onFailed={(message) => setNotice({ tone: 'error', message })}
        />

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

      {permits(membership.permissions.events, 'view_only') ? <CalendarFeed /> : null}

      {/* Tasks assigned to you (issue #356 / 14.4) — a plain link for now;
          the dashboard (issue #359) will fold this in later. */}
      {permits(membership.permissions.tasks, 'view_only') ? (
        <>
          <Heading level={2}>Your tasks</Heading>
          <Link
            href="/my-tasks"
            style={[
              styles.body,
              { color: theme.colors.primary, fontFamily: theme.typography.body },
            ]}
          >
            See what’s assigned to you, across every case you can reach.
          </Link>
        </>
      ) : null}

      {/* Collapsed, and last. It was on the home screen while the pipeline was
          the product; see the component for why it survives at all. */}
      <MePanel />
    </AppShell>
  );
}

/**
 * The ICS feed (issue 14.6 / #358): one link a calendar application
 * subscribes to, carrying every event and deadline this person may see.
 *
 * THE LINK IS SHOWN ONCE, when it is made. The server stores only a hash of
 * the secret in it, so there is no "show me again" — there is "make a new
 * one", which retires the old link wherever it was pasted. That is the
 * revocation story, and it is why the copy here says so out loud.
 */
function CalendarFeed() {
  const theme = useTheme();
  const { call } = useApi();
  const [feedUrl, setFeedUrl] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  const mint = async () => {
    setBusy(true);
    setNotice(null);
    try {
      const result = await call((client) => client.mintCalendarToken());
      if (result.ok) {
        setFeedUrl(result.value.feedUrl);
        setNotice({
          tone: 'saved',
          message: 'Your feed link is ready. Any older link no longer works.',
        });
      }
    } catch {
      setNotice({ tone: 'error', message: 'Could not create a feed link. Please try again.' });
    } finally {
      setBusy(false);
    }
  };

  const revoke = async () => {
    setBusy(true);
    setNotice(null);
    try {
      const result = await call((client) => client.revokeCalendarToken());
      if (result.ok) {
        setFeedUrl(null);
        setNotice({ tone: 'saved', message: 'Your feed link is revoked.' });
      }
    } catch {
      setNotice({ tone: 'error', message: 'Could not revoke the feed link. Please try again.' });
    } finally {
      setBusy(false);
    }
  };

  return (
    <View style={styles.feed}>
      <Heading level={2}>Calendar feed</Heading>
      <Text style={[styles.body, muted]}>
        Subscribe your calendar application to a link that carries every event and deadline you can
        see in Insolvia. The link is shown once; making a new one retires the old.
      </Text>
      {feedUrl === null ? null : (
        <Text
          selectable
          accessibilityLabel="Calendar feed link"
          style={[styles.feedUrl, { color: theme.colors.ink, fontFamily: theme.typography.mono }]}
        >
          {feedUrl}
        </Text>
      )}
      <View style={styles.actions}>
        <Button size="lg" onPress={() => void mint()} disabled={busy}>
          {feedUrl === null ? 'Create feed link' : 'Make a new link'}
        </Button>
        <Button size="lg" intent="secondary" onPress={() => void revoke()} disabled={busy}>
          Revoke feed link
        </Button>
      </View>
      {notice === null ? null : (
        <Text
          aria-live="polite"
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
  );
}

const ADDRESS_PARTS = [
  ['line1', 'Street'],
  ['line2', 'Apartment, suite or unit'],
  ['city', 'City'],
  ['state', 'State'],
  ['postal_code', 'ZIP code'],
] as const;

/**
 * Your standing signature block (issue #360): the B101 Part 7 lines that do
 * not change from case to case — bar number and state, and the firm lines.
 * Self-service on `PATCH /v1/me` like the name, because a bar number is the
 * attorney's own fact; the petition screen's "use firm default" copies it
 * onto a case. Every field is optional, and the whole block is sent on save
 * (the server prunes blanks), or `null` when every field is blank — which is
 * how a block is cleared.
 */
function SignatureBlockForm({
  initial,
  onSaved,
  onFailed,
}: {
  initial: SignatureBlock | null;
  onSaved: (principal: Parameters<ReturnType<typeof useMeActions>['adopt']>[0]) => void;
  onFailed: (message: string) => void;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const [block, setBlock] = useState<SignatureBlock>(initial ?? {});
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [saving, setSaving] = useState(false);

  const setPart = (key: keyof SignatureBlock, value: string) =>
    setBlock((current) => ({ ...current, [key]: value === '' ? undefined : value }));
  const setAddress = (key: keyof Address, value: string) =>
    setBlock((current) => ({
      ...current,
      address: { ...current.address, [key]: value === '' ? undefined : value },
    }));

  const save = async () => {
    setSaving(true);
    setFieldErrors({});
    try {
      const address = Object.fromEntries(
        Object.entries(block.address ?? {}).filter(([, v]) => v !== undefined),
      ) as Address;
      const filled: SignatureBlock = {
        ...Object.fromEntries(
          Object.entries(block).filter(([k, v]) => k !== 'address' && v !== undefined),
        ),
        ...(Object.keys(address).length > 0 ? { address } : {}),
      };
      const signatureBlock = Object.keys(filled).length > 0 ? filled : null;
      const result = await call((client) => client.updateMe({ signatureBlock }));
      if (result.ok) {
        setBlock(result.value.firm?.signatureBlock ?? {});
        onSaved(result.value);
      }
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        // Keyed `signatureBlock.<field>` by the server; the same paths the
        // fields below are named by.
        setFieldErrors(cause.fields);
      } else {
        onFailed('Could not save your signature block. Please try again.');
      }
    } finally {
      setSaving(false);
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const error = (path: string) => fieldErrors[`signatureBlock.${path}`];

  return (
    <View style={styles.form}>
      <Heading level={2}>Signature block</Heading>
      <Text style={[styles.body, muted]}>
        The lines B101 Part 7 prints for you that do not change from case to case. The petition
        screen’s “Use firm default” copies them onto a case for you to check and save.
      </Text>
      <Field.Root name="bar_number" invalid={Boolean(error('bar_number'))}>
        <Field.Label>Bar number</Field.Label>
        <Input value={block.bar_number ?? ''} onValueChange={(v) => setPart('bar_number', v)} />
        {error('bar_number') ? <Field.Error match>{error('bar_number')}</Field.Error> : null}
      </Field.Root>
      <Field.Root name="bar_state" invalid={Boolean(error('bar_state'))}>
        <Field.Label>Bar state</Field.Label>
        <Input
          value={block.bar_state ?? ''}
          onValueChange={(v) => setPart('bar_state', v)}
          autoCapitalize="characters"
        />
        <Field.Description>Two letters, like FL.</Field.Description>
        {error('bar_state') ? <Field.Error match>{error('bar_state')}</Field.Error> : null}
      </Field.Root>
      <Field.Root name="firm_name" invalid={Boolean(error('firm_name'))}>
        <Field.Label>Firm name on filings</Field.Label>
        <Input value={block.firm_name ?? ''} onValueChange={(v) => setPart('firm_name', v)} />
        <Field.Description>Leave blank to use the firm’s letterhead.</Field.Description>
        {error('firm_name') ? <Field.Error match>{error('firm_name')}</Field.Error> : null}
      </Field.Root>
      {ADDRESS_PARTS.map(([part, label]) => (
        <Field.Root key={part} name={part} invalid={Boolean(error(`address.${part}`))}>
          <Field.Label>{`Address — ${label}`}</Field.Label>
          <Input value={block.address?.[part] ?? ''} onValueChange={(v) => setAddress(part, v)} />
          {error(`address.${part}`) ? (
            <Field.Error match>{error(`address.${part}`)}</Field.Error>
          ) : null}
        </Field.Root>
      ))}
      <Field.Root name="phone" invalid={Boolean(error('phone'))}>
        <Field.Label>Phone on filings</Field.Label>
        <Input value={block.phone ?? ''} onValueChange={(v) => setPart('phone', v)} />
        {error('phone') ? <Field.Error match>{error('phone')}</Field.Error> : null}
      </Field.Root>
      <Field.Root name="email" invalid={Boolean(error('email'))}>
        <Field.Label>Email on filings</Field.Label>
        <Input value={block.email ?? ''} onValueChange={(v) => setPart('email', v)} type="email" />
        {error('email') ? <Field.Error match>{error('email')}</Field.Error> : null}
      </Field.Root>
      <View style={styles.actions}>
        <Button size="lg" onPress={() => void save()} disabled={saving}>
          {saving ? 'Saving…' : 'Save signature block'}
        </Button>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
  },
  feed: {
    gap: spacing.sm,
    marginTop: spacing.lg,
  },
  feedUrl: {
    fontSize: fontSizes.caption,
    lineHeight: fontSizes.caption * 1.5,
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
