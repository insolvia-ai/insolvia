import type { ExtractionCandidate, PermissionLevel } from '@insolvia-ai/api-client';
import { ApiValidationException, permits } from '@insolvia-ai/api-client';
import { Badge, Button, Field, Input } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMembership } from '@/api/me';
import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { openDownload } from '@/screens/documents/browser';
import { fontSizes, spacing, useTheme } from '@/theme';

type Queue =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly candidates: readonly ExtractionCandidate[] }
  | { readonly kind: 'error'; readonly message: string };

/** One reviewable line of a candidate's payload, flattened for display. */
interface PayloadRow {
  /** The dotted path (`address.city`) — the correction's write key. */
  readonly path: string;
  readonly label: string;
  readonly value: string;
  /** Only scalar strings are editable in-card; the rest reads as text. */
  readonly editable: boolean;
}

/** What each entity type is called to a human. Falls back to the raw name. */
const ENTITY_LABELS: Record<string, string> = {
  creditors: 'Creditor',
  claims: 'Debt',
  employments: 'Employer',
  pay_period_records: 'Pay period',
  assets: 'Asset',
  income_summaries: 'Income summary',
};

function entityLabel(entityType: string): string {
  return ENTITY_LABELS[entityType] ?? entityType.replace(/_/g, ' ');
}

function fieldLabel(path: string): string {
  const words = path.split('.').map((segment) => segment.replace(/_/g, ' '));
  const joined = words.join(' · ');
  return joined.charAt(0).toUpperCase() + joined.slice(1);
}

/**
 * A candidate payload as label/value rows, one level of nesting flattened
 * (`address.city`). Arrays — deduction lines, notice parties — render as a
 * read-only summary: correcting one belongs to intake after acceptance, not
 * to a card-sized editor pretending otherwise.
 */
function flattenPayload(payload: Readonly<Record<string, unknown>>): readonly PayloadRow[] {
  const rows: PayloadRow[] = [];
  const walk = (value: unknown, path: string) => {
    if (typeof value === 'string') {
      rows.push({ path, label: fieldLabel(path), value, editable: true });
      return;
    }
    if (typeof value === 'number') {
      rows.push({ path, label: fieldLabel(path), value: String(value), editable: false });
      return;
    }
    if (typeof value === 'boolean') {
      rows.push({ path, label: fieldLabel(path), value: value ? 'Yes' : 'No', editable: false });
      return;
    }
    if (Array.isArray(value)) {
      rows.push({
        path,
        label: fieldLabel(path),
        value: value
          .map((entry) =>
            typeof entry === 'object' && entry !== null
              ? Object.entries(entry as Record<string, unknown>)
                  .filter(([key, member]) => key !== 'id' && typeof member === 'string')
                  .map(([, member]) => member as string)
                  .join(' ')
              : String(entry),
          )
          .join('; '),
        editable: false,
      });
      return;
    }
    if (typeof value === 'object' && value !== null) {
      for (const [key, member] of Object.entries(value as Record<string, unknown>)) {
        walk(member, path === '' ? key : `${path}.${key}`);
      }
    }
  };
  walk(payload, '');
  return rows;
}

/** A deep copy of `payload` with the drafts written back in by dotted path.
 *  A draft blanked to '' removes the field — "the model misread this and
 *  there is no value" is a legitimate correction. */
function applyDrafts(
  payload: Readonly<Record<string, unknown>>,
  drafts: Readonly<Record<string, string>>,
): Record<string, unknown> {
  const copy = JSON.parse(JSON.stringify(payload)) as Record<string, unknown>;
  for (const [path, draft] of Object.entries(drafts)) {
    const segments = path.split('.');
    let cursor: Record<string, unknown> = copy;
    for (const segment of segments.slice(0, -1)) {
      const next = cursor[segment];
      if (typeof next !== 'object' || next === null || Array.isArray(next)) {
        cursor[segment] = {};
      }
      cursor = cursor[segment] as Record<string, unknown>;
    }
    const leaf = segments[segments.length - 1]!;
    if (draft.trim() === '') {
      delete cursor[leaf];
    } else {
      cursor[leaf] = draft;
    }
  }
  return copy;
}

/**
 * `/cases/<id>/extraction-review` — the human confirmation that turns
 * extracted candidates into case data (issue #89 / 8.9).
 *
 * The queue lists what the machines proposed — document extraction (8.7/8.8)
 * and MCP agent proposals alike — with the source context a reviewer needs to
 * verify each record: which document, which page, how confident the model
 * was, and a button that opens the source itself. Every record is reviewed
 * ON ITS OWN — accept as proposed, correct then accept, or reject — because
 * the API offers no bulk path on purpose: review must stay cheap, never
 * blind.
 *
 * WHO MAY CONFIRM IS A PERMISSION. The `extraction_review` feature defaults
 * to hidden, so this screen says so plainly instead of rendering a queue the
 * API would refuse; `view_only` sees the queue with no buttons; `add_edit`
 * reviews. The server enforces the same three levels — this gate is honesty,
 * not security.
 */
export function ExtractionReview({ caseId }: { readonly caseId: string }) {
  const theme = useTheme();
  const { call } = useApi();
  const membership = useMembership();

  const [queue, setQueue] = useState<Queue>({ kind: 'loading' });
  const [documentNames, setDocumentNames] = useState<Readonly<Record<string, string>>>({});
  const [editingId, setEditingId] = useState<string | null>(null);
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [busyId, setBusyId] = useState<string | null>(null);
  const [activity, setActivity] = useState('');
  const [actionError, setActionError] = useState<string | null>(null);

  const level: PermissionLevel =
    membership != null ? membership.permissions.extraction_review : 'hidden';
  const mayView = membership != null && permits(level, 'view_only');
  const mayReview = membership != null && permits(level, 'add_edit');
  const mayReadDocuments =
    membership != null && permits(membership.permissions.documents, 'view_only');

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.listExtractionCandidates(caseId, 'pending'));
      if (result.ok) {
        setQueue({ kind: 'ready', candidates: result.value });
      }
    } catch {
      setQueue({ kind: 'error', message: 'Could not load this case’s review queue.' });
    }
  }, [call, caseId]);

  useEffect(() => {
    if (!mayView) {
      return;
    }
    void load();
  }, [load, mayView]);

  // Document names are context, not the queue: fetched once, and only when
  // the reviewer may read documents at all — a 403 here must not take the
  // queue down with it.
  useEffect(() => {
    if (!mayView || !mayReadDocuments) {
      return;
    }
    let cancelled = false;
    void (async () => {
      try {
        const result = await call((client) => client.listDocuments(caseId));
        if (!cancelled && result.ok) {
          const names: Record<string, string> = {};
          for (const entry of result.value) {
            names[entry.id] = entry.fileName;
          }
          setDocumentNames(names);
        }
      } catch {
        // Names are a courtesy; the queue renders the id-free fallback.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, caseId, mayView, mayReadDocuments]);

  const settleOutcome = useCallback(
    (candidateId: string, message: string) => {
      setActivity(message);
      setEditingId((current) => (current === candidateId ? null : current));
      setDrafts({});
      setQueue((current) =>
        current.kind === 'ready'
          ? {
              kind: 'ready',
              candidates: current.candidates.filter((entry) => entry.id !== candidateId),
            }
          : current,
      );
    },
    [setQueue],
  );

  const review = async (
    candidate: ExtractionCandidate,
    action: 'accept' | 'reject',
    correctedPayload?: Readonly<Record<string, unknown>>,
  ) => {
    setActionError(null);
    setBusyId(candidate.id);
    try {
      const result = await call((client) =>
        client.reviewExtractionCandidate(caseId, candidate.id, {
          action,
          ...(correctedPayload === undefined ? {} : { correctedPayload }),
        }),
      );
      if (result.ok) {
        settleOutcome(
          candidate.id,
          action === 'reject'
            ? `Rejected the ${entityLabel(candidate.entityType).toLowerCase()}.`
            : correctedPayload === undefined
              ? `Accepted the ${entityLabel(candidate.entityType).toLowerCase()} into the case.`
              : `Saved your corrections and accepted the ${entityLabel(candidate.entityType).toLowerCase()}.`,
        );
      }
    } catch (error) {
      if (error instanceof ApiValidationException) {
        // The confirm-the-creditor-first refusal, and any correction the
        // parser refused — the server's words, per field.
        setActionError(Object.values(error.fields)[0] ?? 'That review was refused.');
      } else if (
        typeof error === 'object' &&
        error !== null &&
        'statusCode' in error &&
        (error as { statusCode?: unknown }).statusCode === 409
      ) {
        setActionError('Someone else reviewed this record first. The queue was reloaded.');
        void load();
      } else {
        setActionError('Could not save that review. Please try again.');
      }
    } finally {
      setBusyId((current) => (current === candidate.id ? null : current));
    }
  };

  const openSource = async (candidate: ExtractionCandidate) => {
    if (candidate.documentId === undefined) {
      return;
    }
    setActionError(null);
    try {
      const documentId = candidate.documentId;
      const result = await call((client) => client.getDocumentUrl(caseId, documentId));
      if (result.ok && !openDownload(result.value.url, documentNames[documentId] ?? 'document')) {
        setActionError('Could not open the source document — this needs a web browser.');
      }
    } catch {
      setActionError('Could not open the source document. Please try again.');
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const danger = { color: theme.colors.danger, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  if (membership === undefined) {
    return (
      <AppShell>
        <Heading level={1}>Extraction review</Heading>
        <Text style={[styles.body, muted]}>Loading…</Text>
      </AppShell>
    );
  }

  if (!mayView) {
    return (
      <AppShell>
        <Heading level={1}>Extraction review</Heading>
        <Text style={[styles.body, muted]}>
          Extraction review is not enabled for your account. A firm administrator can grant it from
          the firm screen.
        </Text>
      </AppShell>
    );
  }

  const sourceLine = (candidate: ExtractionCandidate): string => {
    const parts: string[] = [];
    if (candidate.origin.channel === 'extraction') {
      parts.push(`Extracted by ${candidate.origin.clientId}`);
    } else {
      parts.push('Proposed by an agent');
    }
    if (candidate.documentId !== undefined) {
      const name = documentNames[candidate.documentId];
      parts.push(name === undefined ? 'from an uploaded document' : `from ${name}`);
    }
    if (candidate.locatorPage !== undefined) {
      parts.push(`page ${candidate.locatorPage}`);
    }
    return parts.join(' · ');
  };

  return (
    <AppShell>
      <Heading level={1}>Extraction review</Heading>
      <Text style={[styles.body, muted]}>
        Nothing extracted enters the case until a person confirms it. Each record below was read
        from a document or proposed by an agent — check it against its source, then accept it,
        correct it, or reject it. Accepted records land in the case marked as machine-read and
        confirmed by you; rejections and corrections are kept, because they are how extraction gets
        better.
      </Text>
      {!mayReview ? (
        <Text style={[styles.body, muted]}>
          Your access is read-only: you can see the queue, and confirming is reserved for colleagues
          your firm has granted it to.
        </Text>
      ) : null}

      <Text aria-live="polite" style={[styles.status, muted]}>
        {activity}
      </Text>
      <Text aria-live="assertive" style={[styles.status, danger]}>
        {actionError ?? (queue.kind === 'error' ? queue.message : '')}
      </Text>

      <Heading level={2}>Awaiting review</Heading>
      {queue.kind === 'loading' ? <Text style={[styles.body, muted]}>Loading…</Text> : null}
      {queue.kind === 'ready' && queue.candidates.length === 0 ? (
        <Text style={[styles.body, muted]}>
          Nothing is waiting. Upload a credit report or pay stubs on the documents screen and
          extraction will fill this queue.
        </Text>
      ) : null}
      {queue.kind === 'ready' && queue.candidates.length > 0 ? (
        <View role="list" style={styles.list}>
          {queue.candidates.map((candidate) => {
            const editing = editingId === candidate.id;
            const rows = flattenPayload(candidate.payload);
            const label = entityLabel(candidate.entityType);
            const busy = busyId === candidate.id;
            return (
              <View
                role="listitem"
                key={candidate.id}
                style={[styles.card, { borderColor: theme.colors.line }]}
              >
                <View style={styles.cardHeader}>
                  <Text style={[styles.cardTitle, ink]}>{label}</Text>
                  {candidate.confidence !== undefined ? (
                    <Badge intent={candidate.confidence >= 0.8 ? 'neutral' : 'warning'} size="sm">
                      {`${Math.round(candidate.confidence * 100)}% confident`}
                    </Badge>
                  ) : null}
                </View>
                <Text style={[styles.cardMeta, muted]}>{sourceLine(candidate)}</Text>

                {editing ? (
                  <View style={styles.fields}>
                    {rows.map((row) =>
                      row.editable ? (
                        <Field.Root key={row.path} name={`${candidate.id}-${row.path}`}>
                          <Field.Label>{row.label}</Field.Label>
                          <Input
                            value={drafts[row.path] ?? row.value}
                            onValueChange={(value) => {
                              setDrafts((current) => ({ ...current, [row.path]: value }));
                            }}
                          />
                        </Field.Root>
                      ) : (
                        <View key={row.path} style={styles.fieldRow}>
                          <Text style={[styles.fieldLabel, muted]}>{row.label}</Text>
                          <Text style={[styles.body, ink]}>{row.value}</Text>
                        </View>
                      ),
                    )}
                  </View>
                ) : (
                  <View style={styles.fields}>
                    {rows.map((row) => (
                      <View key={row.path} style={styles.fieldRow}>
                        <Text style={[styles.fieldLabel, muted]}>{row.label}</Text>
                        <Text style={[styles.body, ink]}>{row.value}</Text>
                      </View>
                    ))}
                  </View>
                )}

                <View style={styles.actions}>
                  {candidate.documentId !== undefined && mayReadDocuments ? (
                    <Button
                      size="lg"
                      intent="secondary"
                      onPress={() => {
                        void openSource(candidate);
                      }}
                      aria-label={`Open the source document for this ${label.toLowerCase()}`}
                    >
                      View source
                    </Button>
                  ) : null}
                  {mayReview && !editing ? (
                    <>
                      <Button
                        size="lg"
                        disabled={busy}
                        onPress={() => {
                          void review(candidate, 'accept');
                        }}
                        aria-label={`Accept this ${label.toLowerCase()} into the case`}
                      >
                        {busy ? 'Saving…' : 'Accept'}
                      </Button>
                      <Button
                        size="lg"
                        intent="secondary"
                        disabled={busy}
                        onPress={() => {
                          setEditingId(candidate.id);
                          setDrafts({});
                        }}
                        aria-label={`Correct this ${label.toLowerCase()} before accepting`}
                      >
                        Correct
                      </Button>
                      <Button
                        size="lg"
                        intent="secondary"
                        disabled={busy}
                        onPress={() => {
                          void review(candidate, 'reject');
                        }}
                        aria-label={`Reject this ${label.toLowerCase()}`}
                      >
                        Reject
                      </Button>
                    </>
                  ) : null}
                  {mayReview && editing ? (
                    <>
                      <Button
                        size="lg"
                        disabled={busy}
                        onPress={() => {
                          void review(candidate, 'accept', applyDrafts(candidate.payload, drafts));
                        }}
                        aria-label={`Accept this ${label.toLowerCase()} with your corrections`}
                      >
                        {busy ? 'Saving…' : 'Accept with corrections'}
                      </Button>
                      <Button
                        size="lg"
                        intent="secondary"
                        disabled={busy}
                        onPress={() => {
                          setEditingId(null);
                          setDrafts({});
                        }}
                        aria-label={`Discard corrections to this ${label.toLowerCase()}`}
                      >
                        Cancel
                      </Button>
                    </>
                  ) : null}
                </View>
              </View>
            );
          })}
        </View>
      ) : null}
    </AppShell>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.xs,
    marginTop: spacing.sm,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  card: {
    borderRadius: spacing.xs,
    borderWidth: 1,
    gap: spacing.xs / 2,
    padding: spacing.sm,
  },
  cardHeader: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.xs,
  },
  cardMeta: {
    fontSize: fontSizes.label,
  },
  cardTitle: {
    fontSize: fontSizes.body,
    fontWeight: '600',
  },
  fieldLabel: {
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
  fieldRow: {
    gap: spacing.xs / 4,
  },
  fields: {
    gap: spacing.xs,
    marginTop: spacing.xs,
  },
  list: {
    gap: spacing.md,
    marginTop: spacing.sm,
  },
  status: {
    fontSize: fontSizes.label,
    minHeight: fontSizes.label * 1.5,
  },
});
