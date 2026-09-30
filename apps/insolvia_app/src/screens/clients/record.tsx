import { ApiException, ApiValidationException, permits } from '@insolvia-ai/api-client';
import type {
  Address,
  FirmClient,
  FirmClientCase,
  FirmMembership,
  OtherName,
} from '@insolvia-ai/api-client';
import { AlertDialog, Badge, Button } from '@insolvia-ai/design-system';
import { Link, useRouter } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { CASE_STATUS_INTENT, CASE_STATUS_LABEL } from '@/components/case-status';
import { displayName, FILING_ROLE_LABEL } from '@/components/client-names';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

import { ClientForm, formFromClient, requestFromForm } from './client-form';
import type { ClientFormState } from './client-form';

type RecordState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly client: FirmClient }
  | { readonly kind: 'missing' }
  | { readonly kind: 'error' };

type CasesState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly cases: readonly FirmClientCase[] }
  | { readonly kind: 'error' };

function addressLines(address: Address | undefined): string | undefined {
  if (address === undefined) return undefined;
  const cityLine = [
    [address.city, address.state].filter((part) => part !== undefined && part !== '').join(', '),
    address.postal_code,
  ]
    .filter((part) => part !== undefined && part !== '')
    .join(' ');
  const lines = [address.line1, address.line2, cityLine].filter(
    (part): part is string => part !== undefined && part !== '',
  );
  const county = address.county === undefined ? '' : ` (${address.county} County)`;
  return lines.length === 0 ? undefined : lines.join('\n') + county;
}

function otherNames(names: readonly OtherName[] | undefined): string | undefined {
  const rendered = (names ?? [])
    .map((name) =>
      [name.given, name.middle, name.surname].filter((part) => part !== undefined).join(' '),
    )
    .filter((name) => name !== '');
  return rendered.length === 0 ? undefined : rendered.join('\n');
}

/**
 * `/clients/<id>` — one client's record (ADR 0022 / #354): who they are, and
 * the cases the caller may open for them.
 *
 * WHOLE-RECORD EDITS. "Edit" swaps the details for the same form "Add
 * client" uses, and "Save changes" is one `PUT` of the whole record — the
 * API's contract (a field the body omits is cleared), which is why the form
 * carries the aliases it does not show (`requestFromForm`). The status is not
 * in that body: archive and restore are their own write (`PUT …/status`), so
 * an edit form left open across an archive cannot quietly un-archive anyone.
 *
 * ARCHIVE, NEVER DELETE. Archiving asks first (an `AlertDialog`, no
 * tap-outside dismissal), says what it does and does not do, and is undone by
 * "Restore". An archived client cannot have a case opened for them — the
 * server refuses, so "Start a case" is not offered until they are restored.
 *
 * THE TAX ID IS THE LAST FOUR, OR NOTHING. The full value is sealed on a case
 * (#382); a client screen shows only what `tax_id_last_four` says, and
 * cannot set it.
 *
 * THE CASES ARE THE ONES YOU MAY SEE, AND ONLY THOSE. The list comes from
 * `GET /v1/firm/clients/<id>/cases`, filtered per case; the empty state says
 * "none you can open", never a count of the rest — the count would be the
 * enumeration ADR 0009's 404 hides. Each case says which debtor this client
 * is on it, since a joint case is also on the other client's record.
 */
export function ClientRecord({
  membership,
  clientId,
}: {
  membership: FirmMembership;
  clientId: string;
}) {
  const theme = useTheme();
  const router = useRouter();
  const { call } = useApi();

  const [record, setRecord] = useState<RecordState>({ kind: 'loading' });
  const [cases, setCases] = useState<CasesState>({ kind: 'loading' });
  const [editing, setEditing] = useState<ClientFormState | null>(null);
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [status, setStatus] = useState('');
  const [busy, setBusy] = useState(false);
  const [confirmArchive, setConfirmArchive] = useState(false);

  const mayView = permits(membership.permissions.clients, 'view_only');
  const mayEdit = permits(membership.permissions.clients, 'add_edit');
  const maySeeCases = permits(membership.permissions.cases, 'view_only');
  const mayOpenCase = permits(membership.permissions.cases, 'add_edit');

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.getFirmClient(clientId));
      if (result.ok) setRecord({ kind: 'ready', client: result.value });
    } catch (cause) {
      // Another firm's id and one that never existed are the same 404 —
      // deliberately indistinguishable, and so are they here.
      setRecord(
        cause instanceof ApiException && cause.statusCode === 404
          ? { kind: 'missing' }
          : { kind: 'error' },
      );
    }
  }, [call, clientId]);

  useEffect(() => {
    if (mayView) void load();
  }, [load, mayView]);

  useEffect(() => {
    if (!mayView || !maySeeCases) return;
    let cancelled = false;
    void (async () => {
      try {
        const result = await call((client) => client.listFirmClientCases(clientId));
        if (result.ok && !cancelled) setCases({ kind: 'ready', cases: result.value });
      } catch {
        if (!cancelled) setCases({ kind: 'error' });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, clientId, mayView, maySeeCases]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };
  const danger = { color: theme.colors.danger, fontFamily: theme.typography.body };

  if (!mayView) {
    return (
      <AppShell>
        <Heading level={1}>Client</Heading>
        <Text style={[styles.body, muted]}>
          Your firm has not given you access to its client list. Ask one of your firm’s
          administrators if you need it.
        </Text>
      </AppShell>
    );
  }

  if (record.kind !== 'ready') {
    return (
      <AppShell>
        <Heading level={1}>{record.kind === 'missing' ? 'Client not found' : 'Client'}</Heading>
        <Text
          aria-live={record.kind === 'loading' ? 'polite' : 'assertive'}
          style={[styles.body, record.kind === 'error' ? danger : muted]}
        >
          {record.kind === 'loading'
            ? 'Loading the client…'
            : record.kind === 'missing'
              ? 'There is no such client in your firm.'
              : 'Could not load this client.'}
        </Text>
        <Link
          href="/clients"
          style={[styles.link, { color: theme.colors.primary, fontFamily: theme.typography.body }]}
        >
          All clients
        </Link>
      </AppShell>
    );
  }

  const client = record.client;
  const archived = client.status === 'archived';

  const save = async (form: ClientFormState) => {
    setBusy(true);
    setErrors({});
    setStatus('Saving…');
    try {
      const result = await call((api) =>
        api.updateFirmClient(client.id, requestFromForm(form, client)),
      );
      if (!result.ok) {
        setStatus('');
        return;
      }
      setRecord({ kind: 'ready', client: result.value });
      setEditing(null);
      setStatus('Saved');
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setErrors(cause.fields);
        setStatus('Some answers need attention.');
      } else {
        setStatus('Could not save. Your changes are still here — try again.');
      }
    } finally {
      setBusy(false);
    }
  };

  const setClientStatus = async (next: 'active' | 'archived') => {
    setConfirmArchive(false);
    setBusy(true);
    setStatus(next === 'archived' ? 'Archiving…' : 'Restoring…');
    try {
      const result = await call((api) => api.setFirmClientStatus(client.id, next));
      if (!result.ok) {
        setStatus('');
        return;
      }
      setRecord({ kind: 'ready', client: result.value });
      setStatus(next === 'archived' ? 'Archived' : 'Restored');
    } catch {
      setStatus(
        next === 'archived' ? 'Could not archive. Try again.' : 'Could not restore. Try again.',
      );
    } finally {
      setBusy(false);
    }
  };

  const details: readonly (readonly [string, string | undefined])[] = [
    ['Date of birth', client.date_of_birth],
    ['Email', client.email],
    ['Phone', client.phone],
    ['Mobile', client.mobile],
    ['Lives at', addressLines(client.residence_address)],
    ['Mailing address', addressLines(client.mailing_address)],
    ['Other names used', otherNames(client.other_names_used)],
    [
      'SSN or ITIN',
      client.tax_id_last_four === undefined
        ? 'Not on file — it is entered on a case'
        : `Ending ${client.tax_id_last_four}`,
    ],
    ['Lead source', client.lead_source],
    ['Referred by', client.referred_by],
    ['First retained', client.first_retained_at],
    ['Added', client.created_at.slice(0, 10)],
  ];

  const statusIsProblem =
    status.startsWith('Could not') || status === 'Some answers need attention.';

  return (
    <AppShell>
      <Link
        href="/clients"
        style={[styles.link, { color: theme.colors.primary, fontFamily: theme.typography.body }]}
      >
        All clients
      </Link>
      <View style={styles.titleRow}>
        <Heading level={1}>{displayName(client)}</Heading>
        <Badge intent={archived ? 'neutral' : 'success'} size="sm">
          {archived ? 'Archived' : 'Active'}
        </Badge>
      </View>

      {editing === null ? (
        <View style={styles.actions}>
          {mayOpenCase && !archived ? (
            <Button
              size="lg"
              onPress={() => router.push(`/cases/new?client=${encodeURIComponent(client.id)}`)}
            >
              Start a case for this client
            </Button>
          ) : null}
          {mayEdit ? (
            <>
              <Button
                size="lg"
                intent="secondary"
                disabled={busy}
                onPress={() => {
                  setErrors({});
                  setStatus('');
                  setEditing(formFromClient(client));
                }}
              >
                Edit details
              </Button>
              <Button
                size="lg"
                intent="secondary"
                disabled={busy}
                onPress={() =>
                  archived ? void setClientStatus('active') : setConfirmArchive(true)
                }
              >
                {archived ? 'Restore' : 'Archive'}
              </Button>
            </>
          ) : null}
        </View>
      ) : null}
      {archived ? (
        <Text style={[styles.body, muted]}>
          Archived — not in the active list, and no new case can be opened for them until they are
          restored. Their cases are unchanged.
        </Text>
      ) : null}

      <Text
        aria-live={statusIsProblem ? 'assertive' : 'polite'}
        style={[styles.status, statusIsProblem ? danger : muted]}
      >
        {status}
      </Text>

      <Heading level={2}>Details</Heading>
      {editing === null ? (
        <View role="list" style={styles.details}>
          {details.map(([label, value]) => (
            <View
              key={label}
              role="listitem"
              style={[styles.detail, { borderColor: theme.colors.line }]}
            >
              <Text style={[styles.detailLabel, muted]}>{label}</Text>
              <Text style={[styles.detailValue, value === undefined ? muted : ink]}>
                {value ?? '—'}
              </Text>
            </View>
          ))}
        </View>
      ) : (
        <>
          <ClientForm value={editing} onChange={setEditing} errors={errors} headingLevel={3} />
          <View style={styles.actions}>
            <Button size="lg" disabled={busy} onPress={() => void save(editing)}>
              Save changes
            </Button>
            <Button
              size="lg"
              intent="secondary"
              onPress={() => {
                setErrors({});
                setStatus('');
                setEditing(null);
              }}
            >
              Cancel
            </Button>
          </View>
        </>
      )}

      {maySeeCases ? (
        <>
          <Heading level={2}>Cases</Heading>
          {cases.kind === 'loading' ? (
            <Text aria-live="polite" style={[styles.body, muted]}>
              Loading their cases…
            </Text>
          ) : cases.kind === 'error' ? (
            <Text aria-live="assertive" style={[styles.body, danger]}>
              Could not load their cases.
            </Text>
          ) : cases.cases.length === 0 ? (
            <Text style={[styles.body, muted]}>No cases you can open for this client.</Text>
          ) : (
            <View role="list" style={styles.cases}>
              {cases.cases.map(({ case: matter, filing_role: role }) => (
                <View
                  key={matter.id}
                  role="listitem"
                  style={[styles.caseRow, { borderColor: theme.colors.line }]}
                >
                  <Link
                    href={`/cases/${matter.id}`}
                    aria-label={`Chapter ${matter.chapter} case in ${matter.district}, opened ${matter.createdAt.slice(0, 10)}`}
                    style={[
                      styles.link,
                      { color: theme.colors.primary, fontFamily: theme.typography.body },
                    ]}
                  >
                    {`Chapter ${matter.chapter} · ${matter.district}`}
                  </Link>
                  <Text style={[styles.caseMeta, muted]}>
                    {`${FILING_ROLE_LABEL[role]} · opened ${matter.createdAt.slice(0, 10)}`}
                  </Text>
                  <Badge intent={CASE_STATUS_INTENT[matter.status]} size="sm">
                    {CASE_STATUS_LABEL[matter.status]}
                  </Badge>
                </View>
              ))}
            </View>
          )}
        </>
      ) : null}

      <AlertDialog.Root
        open={confirmArchive}
        onOpenChange={(next) => {
          if (!next) setConfirmArchive(false);
        }}
      >
        <AlertDialog.Popup>
          <AlertDialog.Title>{`Archive ${displayName(client)}?`}</AlertDialog.Title>
          <AlertDialog.Description>
            They leave the active client list and no new case can be opened for them. Their cases,
            and everything in them, are unchanged. You can restore them at any time.
          </AlertDialog.Description>
          <View style={styles.dialogActions}>
            <Button size="lg" onPress={() => void setClientStatus('archived')}>
              Archive
            </Button>
            <AlertDialog.Close>Cancel</AlertDialog.Close>
          </View>
        </AlertDialog.Popup>
      </AlertDialog.Root>
    </AppShell>
  );
}

const styles = StyleSheet.create({
  actions: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  body: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  caseMeta: { flexGrow: 1, fontSize: fontSizes.label },
  caseRow: {
    alignItems: 'center',
    borderBottomWidth: 1,
    columnGap: spacing.md,
    flexDirection: 'row',
    flexWrap: 'wrap',
  },
  cases: { gap: spacing.xs },
  detail: {
    borderBottomWidth: 1,
    columnGap: spacing.md,
    flexDirection: 'row',
    flexWrap: 'wrap',
    paddingVertical: spacing.sm,
  },
  detailLabel: { flexBasis: 180, fontSize: fontSizes.label },
  detailValue: { flexBasis: 240, flexGrow: 1, fontSize: fontSizes.body },
  details: { gap: 0 },
  dialogActions: { flexDirection: 'row', gap: spacing.sm, marginTop: spacing.md },
  link: {
    fontSize: fontSizes.label,
    fontWeight: '600',
    // 44dp, the WCAG 2.5.5 target size — a text link is no exception.
    lineHeight: 44,
  },
  status: { fontSize: fontSizes.label },
  titleRow: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.md },
});
