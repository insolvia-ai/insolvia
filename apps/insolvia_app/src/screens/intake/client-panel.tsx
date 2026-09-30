import { ApiValidationException } from '@insolvia-ai/api-client';
import type { Debtor, FilingRole, FirmClient } from '@insolvia-ai/api-client';
import { AlertDialog, Button, Field, Input, Select } from '@insolvia-ai/design-system';
import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * The debtor section's link to the firm's client directory (ADR 0022, PR 5).
 *
 * A debtor is a COPY of a client: the case keeps its own values, so a filed
 * petition never changes because the directory did. That makes divergence
 * a visible state rather than a bug, and this panel is where it is seen and
 * resolved — by one of two explicit acts, never silently:
 *
 * - **Re-copy from client** — the case's copied fields take the client's
 *   values again. Refused on a filed case (an amendment is a different act).
 * - **Update client from this case** — the directory takes the case's.
 *
 * Both overwrite someone's typing, so both ask first, through `AlertDialog`
 * (an explicit choice, no tap-outside dismissal). Nothing here is
 * app-generic: the directory screens (#354 PR 4) own browsing clients.
 */

/** How a client reads: "Given Surname", or whichever half exists. */
export function clientName(client: Pick<FirmClient, 'name'>): string {
  const { given, middle, surname } = client.name;
  const joined = [given, middle, surname].filter((part) => part !== undefined && part !== '');
  return joined.length === 0 ? 'Unnamed client' : joined.join(' ');
}

const ROOT_LABELS: Readonly<Record<string, string>> = {
  name: 'Name',
  other_names_used: 'Other name',
  residence_address: 'Residence',
  mailing_address: 'Mailing address',
  phone: 'Phone',
  mobile: 'Mobile',
  email: 'Email',
};

const PART_LABELS: Readonly<Record<string, string>> = {
  given: 'first name',
  middle: 'middle name',
  surname: 'last name',
  suffix: 'suffix',
  business_name: 'business name',
  line1: 'street',
  line2: 'apartment, suite or unit',
  city: 'city',
  state: 'state',
  postal_code: 'ZIP code',
  county: 'county',
};

const SEGMENT_RE = /^([a-z][a-z0-9_]*)(?:\[([A-Za-z0-9_-]+)\])?$/;

/** A `differs_from_client` path in words — "Residence · city". */
export function pathLabel(path: string): string {
  const names = path.split('.').map((segment) => SEGMENT_RE.exec(segment)?.[1] ?? segment);
  const root = ROOT_LABELS[names[0] ?? ''] ?? names[0] ?? path;
  const part = names.length > 1 ? PART_LABELS[names[names.length - 1] ?? ''] : undefined;
  return part === undefined ? root : `${root} · ${part}`;
}

/** The value a field path addresses — list elements by their `id`. */
function valueAt(record: unknown, path: string): string | undefined {
  let current: unknown = record;
  for (const segment of path.split('.')) {
    const match = SEGMENT_RE.exec(segment);
    if (match === null || typeof current !== 'object' || current === null) return undefined;
    current = (current as Record<string, unknown>)[match[1] as string];
    const id = match[2];
    if (id !== undefined) {
      current = Array.isArray(current)
        ? current.find((element: { id?: unknown }) => element?.id === id)
        : undefined;
    }
  }
  return typeof current === 'string' && current !== '' ? current : undefined;
}

type Act = 'recopy' | 'update';

export interface LinkedClientProps {
  readonly debtor: Debtor;
  /** The client record, once read — null while it loads or when it cannot be. */
  readonly client: FirmClient | null;
  readonly filed: boolean;
  readonly onRecopy: () => Promise<string | null>;
  readonly onUpdateClient: () => Promise<string | null>;
}

/**
 * Who this debtor is in the directory, where the two records now disagree,
 * and the two acts. `differs_from_client` absent means the caller cannot see
 * the directory, so only the fact of the link is shown.
 */
export function LinkedClient({
  debtor,
  client,
  filed,
  onRecopy,
  onUpdateClient,
}: LinkedClientProps) {
  const theme = useTheme();
  const [confirming, setConfirming] = useState<Act | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };
  const differs = debtor.differs_from_client;

  const run = async (act: Act) => {
    setConfirming(null);
    setBusy(true);
    setError(null);
    try {
      setError(await (act === 'recopy' ? onRecopy() : onUpdateClient()));
    } finally {
      setBusy(false);
    }
  };

  return (
    <View
      style={[
        styles.panel,
        {
          borderColor: theme.colors.line,
          backgroundColor: theme.colors.card,
          borderRadius: theme.radii.lg,
        },
      ]}
    >
      <Heading level={3} size="body">
        {client === null ? 'Linked client' : `Client: ${clientName(client)}`}
      </Heading>

      {differs === undefined ? (
        <Text style={[styles.line, muted]}>
          This debtor was copied from a client in your firm’s directory.
        </Text>
      ) : differs.length === 0 ? (
        <Text style={[styles.line, muted]}>Matches the client record.</Text>
      ) : (
        <>
          <Text style={[styles.line, ink]}>
            {differs.length === 1
              ? 'One field differs from the client record.'
              : `${differs.length} fields differ from the client record.`}
          </Text>
          <View style={styles.rows}>
            {differs.map((path) => (
              <Text key={path} style={[styles.line, muted]}>
                <Text style={[styles.label, ink]}>{pathLabel(path)}</Text>
                {`  This case: ${valueAt(debtor, path) ?? '(empty)'} · Client: ${
                  client === null ? '…' : (valueAt(client, path) ?? '(empty)')
                }`}
              </Text>
            ))}
          </View>
          <View style={styles.actions}>
            <Button
              size="lg"
              intent="secondary"
              disabled={busy || filed}
              onPress={() => setConfirming('recopy')}
            >
              Re-copy from client
            </Button>
            <Button
              size="lg"
              intent="secondary"
              disabled={busy}
              onPress={() => setConfirming('update')}
            >
              Update client from this case
            </Button>
          </View>
          {filed ? (
            <Text style={[styles.line, muted]}>
              This case is filed, so its debtor is not re-copied — a change to a filed petition is
              an amendment.
            </Text>
          ) : null}
        </>
      )}

      <Text
        aria-live="polite"
        style={[styles.line, { color: theme.colors.danger, fontFamily: theme.typography.body }]}
      >
        {error ?? ''}
      </Text>

      <AlertDialog.Root
        open={confirming !== null}
        onOpenChange={(next) => {
          if (!next) setConfirming(null);
        }}
      >
        <AlertDialog.Popup>
          <AlertDialog.Title>
            {confirming === 'update' ? 'Update the client record?' : 'Re-copy from the client?'}
          </AlertDialog.Title>
          <AlertDialog.Description>
            {confirming === 'update'
              ? 'The client record takes this case’s name, other names, addresses, phone numbers ' +
                'and email. Other cases for this client keep their own copies.'
              : 'This case’s name, other names, addresses, phone numbers and email are replaced ' +
                'with the client record’s. What was typed here is lost.'}
          </AlertDialog.Description>
          <View style={styles.dialogActions}>
            <Button size="lg" onPress={() => void run(confirming ?? 'recopy')}>
              {confirming === 'update' ? 'Update client' : 'Re-copy'}
            </Button>
            <AlertDialog.Close>Cancel</AlertDialog.Close>
          </View>
        </AlertDialog.Popup>
      </AlertDialog.Root>
    </View>
  );
}

/** The select's value for "a new client, named below". */
const NEW_CLIENT = '__new_client__';

export interface LinkClientProps {
  readonly caseId: string;
  readonly role: FilingRole;
  /** Clients already on another role of this case — one client, one role. */
  readonly taken: readonly string[];
  /** False for a non-filing spouse, who need not be the firm's client. */
  readonly required: boolean;
  readonly onLinked: (debtor: Debtor) => void;
}

/**
 * Link a client to an empty role (or to a non-filing spouse entered without
 * one): an existing client, or a new one named here and added to the
 * directory first. The server COPIES the client in — the only way a Debtor 2
 * comes to exist (ADR 0022).
 */
export function LinkClient({ caseId, role, taken, required, onLinked }: LinkClientProps) {
  const theme = useTheme();
  const { call } = useApi();
  const [clients, setClients] = useState<readonly FirmClient[] | 'loading' | 'error'>('loading');
  const [choice, setChoice] = useState<string | null>(null);
  const [given, setGiven] = useState('');
  const [surname, setSurname] = useState('');
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [busy, setBusy] = useState(false);
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await call((client) => client.listFirmClients());
        if (!result.ok || cancelled) return;
        const active = result.value.filter((client) => client.status === 'active');
        setClients(active);
        if (active.length === 0) setChoice(NEW_CLIENT);
      } catch {
        if (!cancelled) setClients('error');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call]);

  const link = async () => {
    setBusy(true);
    setErrors({});
    try {
      let clientId = choice;
      if (choice === NEW_CLIENT) {
        const first = given.trim();
        const last = surname.trim();
        const added = await call((client) =>
          client.createFirmClient({
            name: {
              ...(first === '' ? {} : { given: first }),
              ...(last === '' ? {} : { surname: last }),
            },
          }),
        );
        if (!added.ok) return;
        // Chosen from now on, so a retry after a link error does not add the
        // same person twice.
        setClients((current) => (Array.isArray(current) ? [...current, added.value] : current));
        setChoice(added.value.id);
        clientId = added.value.id;
      }
      if (clientId === null) {
        setErrors({ client_id: 'Choose a client.' });
        return;
      }
      const linked = await call((client) => client.linkDebtorClient(caseId, role, clientId));
      if (linked.ok) onLinked(linked.value);
    } catch (cause) {
      setErrors(
        cause instanceof ApiValidationException
          ? cause.fields
          : { client_id: 'Could not link the client. Try again.' },
      );
    } finally {
      setBusy(false);
    }
  };

  if (clients === 'loading') {
    return <Text style={[styles.line, muted]}>Loading your clients…</Text>;
  }
  if (clients === 'error') {
    return (
      <Text
        style={[styles.line, { color: theme.colors.danger, fontFamily: theme.typography.body }]}
      >
        Could not load your client directory. Linking a client needs access to it — ask a firm admin
        to grant “Clients”.
      </Text>
    );
  }

  const options = [
    ...clients
      .filter((client) => !taken.includes(client.id))
      .map((client) => ({ value: client.id, label: clientName(client) })),
    { value: NEW_CLIENT, label: 'New client…' },
  ];

  return (
    <View
      style={[
        styles.panel,
        {
          borderColor: theme.colors.line,
          backgroundColor: theme.colors.card,
          borderRadius: theme.radii.lg,
        },
      ]}
    >
      <Heading level={3} size="body">
        {required ? 'Link a client' : 'Link a client (optional)'}
      </Heading>
      <Text style={[styles.line, muted]}>
        {required
          ? 'A debtor is one of your firm’s clients. Their name and contact details are copied into ' +
            'this case, and stay this case’s own.'
          : 'A non-filing spouse need not be your client. Link one if they are; otherwise enter ' +
            'their details below.'}
      </Text>
      <Field.Root name="client_id" invalid={Boolean(errors.client_id)}>
        <Field.Label>Client</Field.Label>
        <Select
          options={options}
          value={choice}
          onValueChange={setChoice}
          placeholder="Choose a client"
        />
        {errors.client_id ? <Field.Error match>{errors.client_id}</Field.Error> : null}
      </Field.Root>
      {choice === NEW_CLIENT ? (
        <>
          <Field.Root name="clientGiven" invalid={Boolean(errors.name)}>
            <Field.Label>Client’s first name</Field.Label>
            <Input value={given} onValueChange={setGiven} autoCorrect={false} />
          </Field.Root>
          <Field.Root name="clientSurname" invalid={Boolean(errors.name)}>
            <Field.Label>Client’s last name</Field.Label>
            <Input value={surname} onValueChange={setSurname} autoCorrect={false} />
            {errors.name ? <Field.Error match>{errors.name}</Field.Error> : null}
          </Field.Root>
        </>
      ) : null}
      <View style={styles.actions}>
        <Button size="lg" disabled={busy} onPress={() => void link()}>
          {choice === NEW_CLIENT ? 'Add and link client' : 'Link client'}
        </Button>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm, marginTop: spacing.xs },
  dialogActions: { flexDirection: 'row', gap: spacing.sm, marginTop: spacing.md },
  label: { fontWeight: '600' },
  line: { fontSize: fontSizes.label, lineHeight: fontSizes.label * 1.5 },
  panel: {
    borderWidth: 1,
    gap: spacing.sm,
    marginBottom: spacing.lg,
    padding: spacing.md,
  },
  rows: { gap: spacing.xs },
});
