import { ApiException, ApiValidationException, permits } from '@insolvia-ai/api-client';
import type {
  AddFirmUserRequest,
  Firm as FirmRecord,
  FirmFeature,
  FirmMembership,
  FirmRole,
  FirmUser,
  PermissionLevel,
  UpdateFirmUserRequest,
} from '@insolvia-ai/api-client';
import { Button, Field, Input, RadioGroup, Select } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { EnvBadge } from '@/components/env-badge';
import { Heading } from '@/components/heading';
import { appEnvironment, environmentInfo } from '@/config/environment';
import { fontSizes, spacing, useTheme } from '@/theme';

const ROLES: readonly { readonly value: FirmRole; readonly label: string }[] = [
  { value: 'attorney', label: 'Attorney' },
  { value: 'paralegal', label: 'Paralegal' },
  { value: 'staff', label: 'Staff' },
];

const LEVELS: readonly { readonly value: PermissionLevel; readonly label: string }[] = [
  { value: 'hidden', label: 'Hidden' },
  { value: 'view_only', label: 'View only' },
  { value: 'add_edit', label: 'Add & edit' },
];

/**
 * The features this screen lets an administrator set, and the order they read
 * in. `extraction_review` is here and the feature does not exist yet — that is
 * deliberate, and the label says so, because a firm setting it now should know
 * it takes effect later rather than wonder why nothing happened.
 */
const FEATURES: readonly { readonly value: FirmFeature; readonly label: string }[] = [
  { value: 'cases', label: 'Cases' },
  { value: 'intake', label: 'Intake' },
  { value: 'documents', label: 'Documents' },
  { value: 'extraction_review', label: 'Extraction review (not yet available)' },
  { value: 'firm_administration', label: 'Firm administration' },
];

type ListState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly users: readonly FirmUser[] }
  | { readonly kind: 'error'; readonly message: string };

/**
 * What the screen's one live region announces. `saved` exists for the firm
 * rename: its visible effect is the page title changing, which a screen
 * reader does not announce — the region is the announcement.
 */
type FirmNotice = { readonly tone: 'error' | 'saved'; readonly message: string };

type RecordState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly record: FirmRecord }
  | { readonly kind: 'error'; readonly message: string };

/**
 * The firm's own staff list: who is here, what they may reach, and adding
 * somebody new.
 *
 * ## Nothing here names a firm
 *
 * Every request goes to `/v1/firm/...` and the server takes the firm from the
 * caller's token. There is no firm id in any URL, any form, or any state on
 * this screen — a firm id a client could set is a firm id somebody will
 * eventually set to a different one.
 *
 * ## What this screen must not pretend
 *
 * The buttons it hides are a COURTESY, never a control. The server decides,
 * and a control this screen renders for somebody who may not use it produces a
 * 403 rather than access. That is why {@link permits} is used to decide what to
 * SHOW and nothing here re-derives a permission to decide what to send.
 *
 * ## The one refusal that is not a permission failure
 *
 * A change leaving the firm with no active administrator answers **409**, and
 * it is surfaced as its own message. Reporting it as "you may not do this"
 * would be telling the one person who currently may — self-signup is disabled
 * on the pool, so a firm that loses its last administrator cannot appoint one
 * and needs us to fix it by hand.
 */
export function Firm({ membership }: { membership: FirmMembership }) {
  const theme = useTheme();
  const env = environmentInfo(appEnvironment);
  const { call } = useApi();

  const [list, setList] = useState<ListState>({ kind: 'loading' });
  const [notice, setNotice] = useState<FirmNotice | null>(null);
  // The heading follows a rename without waiting for the next /v1/me — the
  // membership prop is a snapshot from mount.
  const [firmName, setFirmName] = useState(membership.name);

  const mayAdminister = permits(membership.permissions.firm_administration, 'view_only');
  const mayChange = permits(membership.permissions.firm_administration, 'add_edit');

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.listFirmUsers());
      if (result.ok) {
        setList({ kind: 'ready', users: result.value });
      }
      // !ok means the session ended and useApi already navigated.
    } catch {
      setList({ kind: 'error', message: 'Could not load your firm’s people.' });
    }
  }, [call]);

  useEffect(() => {
    if (mayAdminister) {
      void load();
    }
  }, [load, mayAdminister]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  if (!mayAdminister) {
    return (
      <AppShell actions={<EnvBadge env={env.name} />}>
        <Heading level={1}>{membership.name}</Heading>
        <Text style={[styles.body, muted]}>
          Managing your firm’s people is an administrator’s job. Ask one of your firm’s
          administrators if you need somebody added or changed.
        </Text>
      </AppShell>
    );
  }

  return (
    <AppShell actions={<EnvBadge env={env.name} />}>
      <Heading level={1}>{firmName}</Heading>

      <FirmDetails editable={mayChange} onNotice={setNotice} onRenamed={setFirmName} />

      {mayChange ? <AddColleague onAdded={load} onNotice={setNotice} /> : null}

      {/* One live region for the whole screen. Several would compete, and a
          screen reader would announce whichever won. */}
      {notice === null ? null : (
        <Text
          aria-live="assertive"
          style={[
            styles.error,
            { color: notice.tone === 'error' ? theme.colors.danger : theme.colors.muted },
          ]}
        >
          {notice.message}
        </Text>
      )}

      <Heading level={2}>People</Heading>
      {list.kind === 'ready' ? (
        <View role="list" style={styles.list}>
          {list.users.map((user) => (
            <View role="listitem" key={user.subject}>
              <Colleague user={user} editable={mayChange} onChanged={load} onNotice={setNotice} />
            </View>
          ))}
        </View>
      ) : (
        <Text
          aria-live={list.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.body, muted]}
        >
          {list.kind === 'loading' ? 'Loading your firm’s people…' : list.message}
        </Text>
      )}
    </AppShell>
  );
}

/**
 * The firm's own record: the name everyone sees, editable at `add_edit`
 * (issue #217).
 *
 * `status` has no control here on purpose. Suspend/reactivate is Insolvia's
 * own operation on the admin portal — a firm suspending itself would be a
 * lockout with no self-service recovery — and the server's parser refuses a
 * status either way; a control would be a button that can only 400.
 */
function FirmDetails({
  editable,
  onNotice,
  onRenamed,
}: {
  editable: boolean;
  onNotice: (notice: FirmNotice | null) => void;
  onRenamed: (name: string) => void;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const [state, setState] = useState<RecordState>({ kind: 'loading' });
  const [name, setName] = useState('');
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const result = await call((client) => client.getFirm());
        if (!cancelled && result.ok) {
          setState({ kind: 'ready', record: result.value });
          setName(result.value.name);
        }
      } catch {
        if (!cancelled) {
          setState({ kind: 'error', message: 'Could not load your firm’s record.' });
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call]);

  const save = async () => {
    setSaving(true);
    setFieldErrors({});
    onNotice(null);
    try {
      const result = await call((client) => client.updateFirm({ name }));
      if (result.ok) {
        setState({ kind: 'ready', record: result.value });
        setName(result.value.name);
        onRenamed(result.value.name);
        onNotice({ tone: 'saved', message: 'Your firm’s name is saved.' });
      }
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setFieldErrors(cause.fields);
      } else {
        onNotice({
          tone: 'error',
          message: 'Could not save your firm’s name. Please try again.',
        });
      }
    } finally {
      setSaving(false);
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  return (
    <View style={styles.form}>
      <Heading level={2}>Firm details</Heading>

      {state.kind !== 'ready' ? (
        <Text
          aria-live={state.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.body, muted]}
        >
          {state.kind === 'loading' ? 'Loading your firm’s record…' : state.message}
        </Text>
      ) : editable ? (
        <>
          <Field.Root name="name" invalid={Boolean(fieldErrors.name)}>
            <Field.Label>Firm name</Field.Label>
            <Input value={name} onValueChange={setName} autoCorrect={false} />
            <Field.Description>
              Shown to everyone in your firm, and on this page’s title. Firm since{' '}
              {state.record.createdAt.slice(0, 10)}.
            </Field.Description>
            {fieldErrors.name ? <Field.Error match>{fieldErrors.name}</Field.Error> : null}
          </Field.Root>
          <View style={styles.actions}>
            <Button size="lg" onPress={() => void save()} disabled={saving}>
              {saving ? 'Saving…' : 'Save firm name'}
            </Button>
          </View>
        </>
      ) : (
        <Text style={[styles.body, muted]}>
          Firm since {state.record.createdAt.slice(0, 10)}. Renaming the firm is an administrator’s
          job.
        </Text>
      )}
    </View>
  );
}

/** The add-a-colleague form. */
function AddColleague({
  onAdded,
  onNotice,
}: {
  onAdded: () => Promise<void>;
  onNotice: (notice: FirmNotice | null) => void;
}) {
  const theme = useTheme();
  const [email, setEmail] = useState('');
  const [firstName, setFirstName] = useState('');
  const [lastName, setLastName] = useState('');
  const [role, setRole] = useState<FirmRole>('paralegal');
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [submitting, setSubmitting] = useState(false);
  const { call } = useApi();

  const submit = async () => {
    setSubmitting(true);
    setFieldErrors({});
    onNotice(null);
    const request: AddFirmUserRequest = { email, firstName, lastName, role };
    try {
      const result = await call((client) => client.addFirmUser(request));
      if (result.ok) {
        setEmail('');
        setFirstName('');
        setLastName('');
        await onAdded();
      }
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        // The server is the source of truth for validation (ADR 0001), so its
        // per-field messages are rendered as-is rather than restated here.
        setFieldErrors(cause.fields);
      } else if (cause instanceof ApiException && cause.statusCode === 409) {
        onNotice({ tone: 'error', message: 'That email address already has an Insolvia account.' });
      } else {
        onNotice({ tone: 'error', message: 'Could not add them. Please try again.' });
      }
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <View style={styles.form}>
      <Heading level={2}>Add a colleague</Heading>

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

      <Field.Root name="email" invalid={Boolean(fieldErrors.email)}>
        <Field.Label>Work email</Field.Label>
        {/* `type="email"` is the cross-platform spelling: the native leaf
            derives the email keyboard and `autoCapitalize="none"` from it, the
            web leaf emits `type="email"` plus the matching `inputMode`. Stating
            the three RN props by hand would only bind the native half. */}
        <Input value={email} onValueChange={setEmail} type="email" autoCorrect={false} />
        <Field.Description>
          They will get an email with a temporary password. This address is also their sign-in name.
        </Field.Description>
        {fieldErrors.email ? <Field.Error match>{fieldErrors.email}</Field.Error> : null}
      </Field.Root>

      {/*
        A radio group rather than a Select: three options, all worth seeing at
        once, and the same reasoning the chapter picker on the case screen
        gives. The Item IS the circle — the label is a sibling, and `hitSlop`
        takes the 20dp target to the 44dp WCAG 2.5.5 floor.
      */}
      <RadioGroup.Root
        aria-label="Role"
        value={role}
        onValueChange={(next) => setRole(next as FirmRole)}
        style={styles.roles}
      >
        {ROLES.map((option) => (
          <View key={option.value} style={styles.roleOption}>
            <RadioGroup.Item value={option.value} aria-label={option.label} hitSlop={12}>
              <RadioGroup.Indicator />
            </RadioGroup.Item>
            <Text style={[styles.roleLabel, { color: theme.colors.ink }]}>{option.label}</Text>
          </View>
        ))}
      </RadioGroup.Root>
      {fieldErrors.role ? (
        <Text aria-live="assertive" style={[styles.error, { color: theme.colors.danger }]}>
          {fieldErrors.role}
        </Text>
      ) : null}

      <View style={styles.actions}>
        <Button size="lg" onPress={submit} disabled={submitting}>
          {submitting ? 'Adding…' : 'Add colleague'}
        </Button>
      </View>
    </View>
  );
}

/** One person, with the controls that change what they may reach. */
function Colleague({
  user,
  editable,
  onChanged,
  onNotice,
}: {
  user: FirmUser;
  editable: boolean;
  onChanged: () => Promise<void>;
  onNotice: (notice: FirmNotice | null) => void;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const [busy, setBusy] = useState(false);

  const change = async (request: UpdateFirmUserRequest) => {
    setBusy(true);
    onNotice(null);
    try {
      const result = await call((client) => client.updateFirmUser(user.subject, request));
      if (result.ok) {
        await onChanged();
      }
    } catch (cause) {
      if (cause instanceof ApiException && cause.statusCode === 409) {
        // NOT a permission failure. See the screen's docstring.
        onNotice({
          tone: 'error',
          message:
            'Your firm must keep at least one active administrator. Appoint another one first.',
        });
      } else {
        onNotice({ tone: 'error', message: 'Could not save that change. Please try again.' });
      }
    } finally {
      setBusy(false);
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  return (
    <View style={styles.person}>
      <Text style={[styles.personName, { color: theme.colors.ink }]}>{user.displayName}</Text>
      <Text style={[styles.personMeta, muted]}>
        {user.email} · {user.role}
        {user.isAdmin ? ' · administrator' : ''}
        {user.status === 'disabled' ? ' · disabled' : ''}
      </Text>

      {editable ? (
        <View style={styles.personControls}>
          {/*
            One Select per feature. The accessible name carries the PERSON as
            well as the feature: without it a screen reader hears "Documents"
            once per colleague and cannot tell whose it is — the same WCAG 2.4.4
            problem a list of identical links has.
          */}
          {FEATURES.map((feature) => (
            <View key={feature.value} style={styles.permissionRow}>
              <Text style={[styles.permissionLabel, muted]}>{feature.label}</Text>
              <Select
                aria-label={`${feature.label} for ${user.displayName}`}
                options={LEVELS.map((level) => ({ value: level.value, label: level.label }))}
                value={user.permissions[feature.value]}
                disabled={busy}
                onValueChange={(next) =>
                  void change({
                    permissions: {
                      ...user.permissions,
                      [feature.value]: next as PermissionLevel,
                    },
                  })
                }
              />
            </View>
          ))}

          <View style={styles.actions}>
            <Button
              size="lg"
              intent="secondary"
              disabled={busy}
              onPress={() => void change({ isAdmin: !user.isAdmin })}
            >
              {user.isAdmin ? 'Remove administrator' : 'Make administrator'}
            </Button>
            <Button
              size="lg"
              intent="secondary"
              disabled={busy}
              onPress={() => void change({ accessAllCases: !user.accessAllCases })}
            >
              {user.accessAllCases ? 'Restrict to linked cases' : 'Give access to every case'}
            </Button>
            <Button
              size="lg"
              intent="secondary"
              disabled={busy}
              onPress={() =>
                void change({ status: user.status === 'active' ? 'disabled' : 'active' })
              }
            >
              {user.status === 'active' ? 'Disable' : 'Re-enable'}
            </Button>
          </View>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
    marginTop: spacing.xs,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  error: {
    fontSize: fontSizes.label,
  },
  form: {
    gap: spacing.md,
    marginBottom: spacing.lg,
    marginTop: spacing.sm,
  },
  list: {
    gap: spacing.lg,
  },
  permissionLabel: {
    fontSize: fontSizes.caption,
  },
  permissionRow: {
    gap: spacing.xs,
    minWidth: 220,
  },
  person: {
    gap: spacing.xs,
  },
  personControls: {
    gap: spacing.sm,
    marginTop: spacing.xs,
  },
  personMeta: {
    fontSize: fontSizes.caption,
  },
  personName: {
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
  roleLabel: {
    fontSize: fontSizes.label,
  },
  roleOption: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.xs,
  },
  roles: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
  },
});
