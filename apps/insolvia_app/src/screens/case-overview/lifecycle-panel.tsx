import {
  ApiException,
  ApiValidationException,
  CASE_TRANSITIONS,
  FILING_WORKER_ACTOR,
  isFiledStatus,
  permits,
} from '@insolvia-ai/api-client';
import type {
  Case,
  CaseStatus,
  CaseStatusChange,
  Document,
  FirmColleague,
  UpdateCaseChanges,
} from '@insolvia-ai/api-client';
import { AlertDialog, Badge, Button, Field, Input } from '@insolvia-ai/design-system';
import { useRouter } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMembership } from '@/api/me';
import { useApi } from '@/api/use-api';
import { useCase } from '@/components/case-shell';
import { CASE_STATUS_INTENT, CASE_STATUS_LABEL } from '@/components/case-status';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

/** The four docket facts this panel edits, keyed as the case carries them. */
const DOCKET_FIELDS = [
  {
    key: 'caseNumber',
    wire: 'case_number',
    label: 'Case number',
    hint: 'As the notice of filing prints it, e.g. 8:26-bk-01234.',
  },
  { key: 'judge', wire: 'judge', label: 'Judge', hint: null },
  { key: 'trustee', wire: 'trustee', label: 'Trustee', hint: null },
  {
    key: 'officeFileNumber',
    wire: 'office_file_number',
    label: 'Office file number',
    hint: 'The firm’s own number for this matter.',
  },
] as const;

type DocketKey = (typeof DOCKET_FIELDS)[number]['key'];
type Docket = Record<DocketKey, string>;

/** What a move is called on its button — the act, not the destination. */
const MOVE_LABEL: Readonly<Record<CaseStatus, string>> = {
  intake: 'Move to intake',
  ready_to_file: 'Mark ready to file',
  filed: 'Mark filed',
  discharged: 'Record discharge',
  dismissed: 'Record dismissal',
  closed: 'Record case closed',
};

function docketOf(matter: Case): Docket {
  return {
    caseNumber: matter.caseNumber ?? '',
    judge: matter.judge ?? '',
    trustee: matter.trustee ?? '',
    officeFileNumber: matter.officeFileNumber ?? '',
  };
}

/**
 * The case's lifecycle, on its overview (issue 14.3 / #355): where it sits
 * between intake and closed (a case starts retained — the funnel before it
 * is the client's, on the client record),
 * the moves it can make, its docket facts, what happened to it and when —
 * and the three acts on the matter as a whole: archive, copy, delete.
 *
 * THE MOVES ARE THE SERVER'S MAP (`CASE_TRANSITIONS`), so a button is only
 * ever offered for a move that will land; the server still decides, and its
 * refusal is shown as it comes. Filing needs the filed date (recorded under
 * "Events and deadlines", where it drives the deadlines) and the case number
 * (here): "Mark filed" sends the case number typed here with it, and a 400
 * naming what is missing is shown beside the field it names.
 *
 * DELETE IS A FIRM ADMIN'S, AND ONLY FOR A CASE THAT NEVER REACHED THE COURT
 * — the button is absent otherwise rather than refused after the fact, and
 * it asks in an `AlertDialog` because there is no undo in the product.
 */
export function LifecyclePanel({ colleagues }: { colleagues: readonly FirmColleague[] }) {
  const theme = useTheme();
  const router = useRouter();
  const { call } = useApi();
  const { caseId, matter, reload } = useCase();
  const membership = useMembership();

  const [docket, setDocket] = useState<Docket>(() => docketOf(matter));
  const [history, setHistory] = useState<readonly CaseStatusChange[] | null>(null);
  const [receipts, setReceipts] = useState<readonly Document[]>([]);
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [status, setStatus] = useState('');
  const [busy, setBusy] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);

  const mayEdit = membership != null && permits(membership.permissions.cases, 'add_edit');
  const mayCopy = mayEdit && permits(membership.permissions.clients, 'view_only');
  const mayDelete = mayEdit && membership.isAdmin && !isFiledStatus(matter.status);

  useEffect(() => {
    setDocket(docketOf(matter));
  }, [matter]);

  const loadHistory = useCallback(async () => {
    try {
      const result = await call((client) => client.listCaseStatusHistory(caseId));
      if (result.ok) setHistory(result.value);
    } catch {
      setHistory([]);
    }
  }, [call, caseId]);

  useEffect(() => {
    void loadHistory();
  }, [loadHistory]);

  // The court-filing receipt the filing worker stores with the case (ADR
  // 0024 PR 8): a `court_notice` document, shown once the case is filed.
  const filed = isFiledStatus(matter.status);
  useEffect(() => {
    if (!filed) return;
    let live = true;
    void (async () => {
      try {
        const result = await call((client) => client.listDocuments(caseId));
        if (live && result.ok) {
          setReceipts(
            result.value.filter(
              (d) =>
                d.status === 'stored' &&
                d.kind === 'court_notice' &&
                d.fileName === RECEIPT_FILE_NAME,
            ),
          );
        }
      } catch {
        // The receipt line is a convenience; the documents screen lists it too.
      }
    })();
    return () => {
      live = false;
    };
  }, [call, caseId, filed]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  const nameOf = (subject: string) =>
    subject === FILING_WORKER_ACTOR
      ? 'Insolvia’s filing service'
      : (colleagues.find((colleague) => colleague.subject === subject)?.displayName ?? subject);

  /** The docket fields as a PATCH: what changed, blanks as `null` (clear). */
  const docketChanges = (): UpdateCaseChanges => {
    const before = docketOf(matter);
    const changes: Record<string, string | null> = {};
    for (const field of DOCKET_FIELDS) {
      const next = docket[field.key].trim();
      if (next !== before[field.key]) changes[field.key] = next === '' ? null : next;
    }
    return changes;
  };

  const save = async (changes: UpdateCaseChanges, done: string) => {
    setBusy(true);
    setErrors({});
    setStatus('Saving…');
    try {
      const result = await call((client) => client.updateCase(caseId, changes));
      if (!result.ok) return;
      setStatus(done);
      await Promise.all([reload(), loadHistory()]);
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setErrors(cause.fields);
        setStatus('Some answers need attention.');
      } else if (cause instanceof ApiException && cause.statusCode === 409) {
        setStatus(serverMessage(cause, 'That move is not possible from here.'));
      } else {
        setStatus('Could not save. Try again.');
      }
    } finally {
      setBusy(false);
    }
  };

  const act = async (run: () => Promise<void>, failed: string) => {
    setBusy(true);
    setStatus('Working…');
    try {
      await run();
    } catch (cause) {
      setStatus(
        cause instanceof ApiException && (cause.statusCode === 409 || cause.statusCode === 403)
          ? serverMessage(cause, failed)
          : failed,
      );
    } finally {
      setBusy(false);
    }
  };

  const archive = (archived: boolean) =>
    act(async () => {
      const result = await call((client) => client.setCaseArchived(caseId, archived));
      if (!result.ok) return;
      setStatus(
        archived ? 'Archived — it has left the working list.' : 'Restored to the working list.',
      );
      await reload();
    }, 'Could not change the archive. Try again.');

  const copy = () =>
    act(async () => {
      const result = await call((client) => client.copyCase(caseId));
      if (result.ok) router.push(`/cases/${result.value.id}`);
    }, 'Could not copy this case. Try again.');

  const remove = () =>
    act(async () => {
      const result = await call((client) => client.deleteCase(caseId));
      if (result.ok) router.replace('/cases');
    }, 'Could not delete this case. Try again.');

  const moves = CASE_TRANSITIONS[matter.status];

  return (
    <View
      style={[
        styles.section,
        {
          backgroundColor: theme.colors.card,
          borderColor: theme.colors.line,
          borderRadius: theme.radii.lg,
        },
      ]}
    >
      <View style={[styles.sectionHead, { borderBottomColor: theme.colors.line }]}>
        <Heading level={2} size="body">
          Lifecycle
        </Heading>
        <View style={styles.badges}>
          <Badge intent={CASE_STATUS_INTENT[matter.status]} size="sm">
            {CASE_STATUS_LABEL[matter.status]}
          </Badge>
          {matter.archivedAt !== undefined ? (
            <Badge intent="neutral" size="sm">
              Archived
            </Badge>
          ) : null}
        </View>
      </View>

      {filed && matter.caseNumber !== undefined ? (
        <View style={styles.filedSummary}>
          <Text style={[styles.rowTitle, ink]} selectable>
            {`Case ${matter.caseNumber}${matter.filedAt !== undefined ? `, filed ${matter.filedAt}` : ''}`}
          </Text>
          {receipts.length > 0 ? (
            <View style={styles.actions}>
              <Text style={[styles.body, muted]}>
                The court-filing receipt is stored with the case’s documents.
              </Text>
              <Button
                size="lg"
                intent="secondary"
                onPress={() => router.push(`/cases/${caseId}/documents`)}
              >
                View the receipt
              </Button>
            </View>
          ) : null}
        </View>
      ) : null}

      {mayEdit && moves.length > 0 ? (
        <View style={styles.actions}>
          {moves.map((to) => (
            <Button
              key={to}
              size="lg"
              intent={to === 'dismissed' ? 'secondary' : 'primary'}
              disabled={busy}
              onPress={() =>
                void save(
                  to === 'filed' ? { ...docketChanges(), status: to } : { status: to },
                  `Now ${CASE_STATUS_LABEL[to].toLowerCase()}`,
                )
              }
            >
              {MOVE_LABEL[to]}
            </Button>
          ))}
        </View>
      ) : null}

      <View style={styles.fields}>
        {DOCKET_FIELDS.map((field) => (
          <Field.Root key={field.key} name={field.wire} invalid={Boolean(errors[field.wire])}>
            <Field.Label>{field.label}</Field.Label>
            <Input
              value={docket[field.key]}
              disabled={!mayEdit || busy}
              onValueChange={(next) => setDocket((current) => ({ ...current, [field.key]: next }))}
            />
            {field.hint !== null ? <Field.Description>{field.hint}</Field.Description> : null}
            {errors[field.wire] ? <Field.Error match>{errors[field.wire]}</Field.Error> : null}
          </Field.Root>
        ))}
        {errors.filed_at ? (
          <Text
            aria-live="assertive"
            style={[styles.body, { color: theme.colors.danger, fontFamily: theme.typography.body }]}
          >
            {`${errors.filed_at} Record it under “Events and deadlines”.`}
          </Text>
        ) : null}
        {mayEdit ? (
          <View style={styles.actions}>
            <Button
              size="lg"
              intent="secondary"
              disabled={busy}
              onPress={() => void save(docketChanges(), 'Docket details saved')}
            >
              Save docket details
            </Button>
          </View>
        ) : null}
      </View>

      <Text
        aria-live={status.includes('attention') ? 'assertive' : 'polite'}
        style={[styles.help, muted]}
      >
        {status}
      </Text>

      <Heading level={3} size="body">
        History
      </Heading>
      {history === null ? (
        <Text style={[styles.body, muted]}>Loading…</Text>
      ) : history.length === 0 ? (
        <Text style={[styles.body, muted]}>No moves yet — the case is where it was opened.</Text>
      ) : (
        <View role="list" style={styles.list}>
          {history.map((change) => (
            <View
              key={`${change.changedAt}-${change.toStatus}`}
              role="listitem"
              style={[styles.row, { borderTopColor: theme.colors.line }]}
            >
              <Text style={[styles.rowTitle, ink]}>{describeChange(change)}</Text>
              <Text style={[styles.rowMeta, muted]}>
                {`${change.changedAt.slice(0, 10)} · ${nameOf(change.changedBy)}${
                  change.filingId !== undefined ? ' · electronic filing' : ''
                }`}
              </Text>
            </View>
          ))}
        </View>
      )}

      {mayEdit ? (
        <View style={[styles.actions, styles.matterActions]}>
          <Button
            size="lg"
            intent="secondary"
            disabled={busy}
            onPress={() => void archive(matter.archivedAt === undefined)}
          >
            {matter.archivedAt === undefined ? 'Archive case' : 'Restore to working list'}
          </Button>
          {mayCopy ? (
            <Button size="lg" intent="secondary" disabled={busy} onPress={() => void copy()}>
              Copy to a new case
            </Button>
          ) : null}
          {mayDelete ? (
            <Button
              size="lg"
              intent="danger"
              disabled={busy}
              onPress={() => setConfirmDelete(true)}
            >
              Delete case
            </Button>
          ) : null}
        </View>
      ) : null}

      <AlertDialog.Root
        open={confirmDelete}
        onOpenChange={(next) => {
          if (!next) setConfirmDelete(false);
        }}
      >
        <AlertDialog.Popup>
          <AlertDialog.Title>Delete this case?</AlertDialog.Title>
          <AlertDialog.Description>
            The case, its debtors, schedules and documents disappear from Insolvia for everyone in
            the firm. Nothing is erased from the firm’s records, but there is no way to bring it
            back from here. A case that has been filed cannot be deleted — archive it instead.
          </AlertDialog.Description>
          <View style={styles.actions}>
            <Button
              size="lg"
              intent="danger"
              disabled={busy}
              onPress={() => {
                setConfirmDelete(false);
                void remove();
              }}
            >
              Delete
            </Button>
            <AlertDialog.Close>Cancel</AlertDialog.Close>
          </View>
        </AlertDialog.Popup>
      </AlertDialog.Root>
    </View>
  );
}

/** The worker's receipt (services/filing core/receipt.RECEIPT_FILE_NAME). */
const RECEIPT_FILE_NAME = 'court-filing-receipt.pdf';

function describeChange(change: CaseStatusChange): string {
  return `${CASE_STATUS_LABEL[change.fromStatus]} → ${CASE_STATUS_LABEL[change.toStatus]}`;
}

/** The server's message without the error-class prefix the api-client adds. */
function serverMessage(cause: ApiException, fallback: string): string {
  return cause.message.replace(/^[A-Za-z]+Error:\s*/, '') || fallback;
}

const styles = StyleSheet.create({
  filedSummary: { gap: spacing.xs, marginTop: spacing.sm },
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm, marginTop: spacing.sm },
  badges: { flexDirection: 'row', gap: spacing.xs },
  body: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  fields: { gap: spacing.md, marginTop: spacing.md },
  help: { fontSize: fontSizes.label, minHeight: fontSizes.label * 1.5, marginVertical: spacing.sm },
  list: { gap: spacing.sm, marginTop: spacing.sm },
  matterActions: { marginTop: spacing.lg },
  row: { borderTopWidth: 1, gap: 2, paddingTop: spacing.sm },
  rowMeta: { fontSize: fontSizes.caption, lineHeight: fontSizes.caption * 1.5 },
  rowTitle: { fontSize: fontSizes.label, fontWeight: '600', lineHeight: fontSizes.label * 1.5 },
  section: { borderWidth: 1, padding: spacing.lg },
  sectionHead: {
    alignItems: 'baseline',
    borderBottomWidth: 1,
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
    justifyContent: 'space-between',
    marginBottom: spacing.md,
    paddingBottom: spacing.md,
  },
});
