import {
  ApiValidationException,
  DOCUMENT_KINDS,
  isDocumentKind,
  permits,
} from '@insolvia-ai/api-client';
import type {
  CaseDocumentRequests,
  DocumentKind,
  DocumentRequest,
  DocumentRequestStatus,
} from '@insolvia-ai/api-client';
import { Badge, Button, Field, Input, Meter, Select, Textarea } from '@insolvia-ai/design-system';
import type { SelectOption } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMembership } from '@/api/me';
import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { KIND_LABELS, kindLabel } from '@/screens/documents';
import { fontSizes, spacing, useTheme } from '@/theme';

type ListState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly value: CaseDocumentRequests }
  | { readonly kind: 'error' };

interface Draft {
  readonly title: string;
  readonly kind: DocumentKind;
  readonly description: string;
}

const EMPTY_DRAFT: Draft = { title: '', kind: 'other', description: '' };

const KIND_OPTIONS: readonly SelectOption[] = DOCUMENT_KINDS.map((kind) => ({
  value: kind,
  label: KIND_LABELS[kind],
}));

const STATUS_BADGE = {
  requested: { intent: 'warning', label: 'Outstanding' },
  received: { intent: 'success', label: 'Arrived' },
  waived: { intent: 'neutral', label: 'Not needed' },
} as const satisfies Record<
  DocumentRequestStatus,
  { intent: 'warning' | 'success' | 'neutral'; label: string }
>;

/**
 * The documents this case has asked its client for (ADR 0023 PR 5 / #364):
 * arrived against outstanding, as a meter and a sentence, then each request
 * with its status.
 *
 * "Request the checklist" is the explicit act that seeds a case from the
 * firm's checklist — not done on case open; `insolvia_core.document_requests`
 * says why — and is safe to press again. A request is received only by an
 * upload against it (the client's through the portal, or staff's on the
 * documents screen); this panel can waive one, reopen one, add one, or
 * withdraw one asked in error.
 *
 * Gated on the `documents` feature — a courtesy; the API re-checks every
 * call. Renders nothing when the caller cannot view documents, TasksPanel's
 * "omit rather than explain".
 */
export function DocumentRequestsPanel({ caseId }: { caseId: string }) {
  const theme = useTheme();
  const { call } = useApi();
  const membership = useMembership();
  const [list, setList] = useState<ListState>({ kind: 'loading' });
  const [draft, setDraft] = useState<Draft | null>(null);
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [status, setStatus] = useState('');
  const [busy, setBusy] = useState(false);

  const level = membership?.permissions.documents;
  const mayView = level !== undefined && permits(level, 'view_only');
  const mayChange = level !== undefined && permits(level, 'add_edit');

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.listDocumentRequests(caseId));
      if (result.ok) setList({ kind: 'ready', value: result.value });
    } catch {
      setList({ kind: 'error' });
    }
  }, [call, caseId]);

  useEffect(() => {
    if (mayView) void load();
  }, [load, mayView]);

  if (!mayView) return null;

  const act = async (pending: string, done: string, run: () => Promise<unknown>) => {
    setBusy(true);
    setStatus(pending);
    try {
      await run();
      setStatus(done);
    } catch {
      setStatus('That did not work. Try again.');
    } finally {
      setBusy(false);
      await load();
    }
  };

  const applyChecklist = () =>
    void act('Requesting the checklist…', 'Checklist requested.', async () => {
      const result = await call((client) => client.applyDocumentChecklist(caseId));
      if (result.ok && result.value.added === 0) {
        setStatus('Every document on the checklist is already requested.');
      }
    });

  const setRequestStatus = (request: DocumentRequest, next: 'requested' | 'waived') =>
    void act(
      next === 'waived' ? 'Marking not needed…' : 'Reopening…',
      next === 'waived' ? `${request.title}: not needed.` : `${request.title}: requested again.`,
      () => call((client) => client.setDocumentRequestStatus(caseId, request.id, next)),
    );

  const remove = (request: DocumentRequest) =>
    void act('Withdrawing…', `${request.title} withdrawn.`, () =>
      call((client) => client.deleteDocumentRequest(caseId, request.id)),
    );

  const add = async () => {
    if (draft === null) return;
    setBusy(true);
    setErrors({});
    setStatus('Adding…');
    try {
      const description = draft.description.trim();
      const result = await call((client) =>
        client.addDocumentRequest(caseId, {
          title: draft.title,
          kind: draft.kind,
          ...(description === '' ? {} : { description }),
        }),
      );
      if (result.ok) {
        setDraft(null);
        setStatus(`${result.value.title} requested.`);
        await load();
      }
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setErrors(cause.fields);
        setStatus('Some answers need attention.');
      } else {
        setStatus('Could not add it. Try again.');
      }
    } finally {
      setBusy(false);
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

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
      <View style={[styles.head, { borderBottomColor: theme.colors.line }]}>
        <Heading level={2} size="body">
          Requested documents
        </Heading>
      </View>

      <Text
        aria-live={status === 'Some answers need attention.' ? 'assertive' : 'polite'}
        style={[styles.help, muted]}
      >
        {status}
      </Text>

      {list.kind !== 'ready' ? (
        <Text
          aria-live={list.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.body, muted]}
        >
          {list.kind === 'loading'
            ? 'Loading requested documents…'
            : 'Could not load this case’s requested documents.'}
        </Text>
      ) : (
        <View style={styles.body}>
          <ProgressSummary value={list.value} />
          {list.value.requests.length === 0 ? (
            <Text style={[styles.text, muted]}>
              Nothing requested yet. Request your firm’s checklist to ask the client for the
              documents every case needs.
            </Text>
          ) : (
            <View role="list" style={styles.list}>
              {list.value.requests.map((request) => (
                <View
                  key={request.id}
                  role="listitem"
                  style={[styles.row, { borderColor: theme.colors.line }]}
                >
                  <View style={styles.rowBody}>
                    <Text style={[styles.rowTitle, ink]}>{request.title}</Text>
                    <View style={styles.rowMeta}>
                      <Badge intent={STATUS_BADGE[request.status].intent} size="sm">
                        {STATUS_BADGE[request.status].label}
                      </Badge>
                      <Text style={[styles.caption, muted]}>
                        {kindLabel(request.kind)}
                        {request.documentIds.length > 0
                          ? ` · ${String(request.documentIds.length)} file${request.documentIds.length === 1 ? '' : 's'}`
                          : ''}
                      </Text>
                    </View>
                  </View>
                  {mayChange ? (
                    <View style={styles.rowActions}>
                      {request.status === 'waived' ? (
                        <Button
                          size="lg"
                          intent="secondary"
                          disabled={busy}
                          aria-label={`Request ${request.title} again`}
                          onPress={() => setRequestStatus(request, 'requested')}
                        >
                          Request again
                        </Button>
                      ) : (
                        <Button
                          size="lg"
                          intent="secondary"
                          disabled={busy}
                          aria-label={`Mark ${request.title} not needed`}
                          onPress={() => setRequestStatus(request, 'waived')}
                        >
                          Not needed
                        </Button>
                      )}
                      {request.status === 'received' ? (
                        <Button
                          size="lg"
                          intent="secondary"
                          disabled={busy}
                          aria-label={`Ask for ${request.title} again`}
                          onPress={() => setRequestStatus(request, 'requested')}
                        >
                          Ask again
                        </Button>
                      ) : null}
                      <Button
                        size="lg"
                        intent="secondary"
                        disabled={busy}
                        aria-label={`Withdraw ${request.title}`}
                        onPress={() => remove(request)}
                      >
                        Withdraw
                      </Button>
                    </View>
                  ) : null}
                </View>
              ))}
            </View>
          )}

          {mayChange && draft === null ? (
            <View style={styles.actions}>
              <Button size="lg" disabled={busy} onPress={applyChecklist}>
                Request the checklist
              </Button>
              <Button
                size="lg"
                intent="secondary"
                disabled={busy}
                onPress={() => {
                  setErrors({});
                  setDraft(EMPTY_DRAFT);
                }}
              >
                Request another document
              </Button>
            </View>
          ) : null}

          {mayChange && draft !== null ? (
            <View style={styles.form}>
              <Field.Root name="title" invalid={Boolean(errors.title)}>
                <Field.Label>Document</Field.Label>
                <Input
                  value={draft.title}
                  onValueChange={(title) => setDraft({ ...draft, title })}
                />
                {errors.title ? <Field.Error match>{errors.title}</Field.Error> : null}
              </Field.Root>
              <Field.Root name="kind" invalid={Boolean(errors.kind)}>
                <Field.Label>Filed as</Field.Label>
                <Select
                  options={KIND_OPTIONS}
                  value={draft.kind}
                  onValueChange={(kind) => {
                    if (typeof kind === 'string' && isDocumentKind(kind))
                      setDraft({ ...draft, kind });
                  }}
                />
                {errors.kind ? <Field.Error match>{errors.kind}</Field.Error> : null}
              </Field.Root>
              <Field.Root name="description" invalid={Boolean(errors.description)}>
                <Field.Label>What to tell the client</Field.Label>
                <Textarea
                  value={draft.description}
                  onValueChange={(description) => setDraft({ ...draft, description })}
                />
                {errors.description ? <Field.Error match>{errors.description}</Field.Error> : null}
              </Field.Root>
              <View style={styles.actions}>
                <Button size="lg" disabled={busy} onPress={() => void add()}>
                  Request it
                </Button>
                <Button size="lg" intent="secondary" disabled={busy} onPress={() => setDraft(null)}>
                  Cancel
                </Button>
              </View>
            </View>
          ) : null}
        </View>
      )}
    </View>
  );
}

/** "3 of 6 arrived", a meter, and what is outstanding — or nothing to measure. */
export function ProgressSummary({
  value,
}: {
  value: { readonly progress: CaseDocumentRequests['progress'] };
}) {
  const theme = useTheme();
  const { total, received, outstanding, waived } = value.progress;
  if (total === 0 && waived === 0) return null;
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };
  const label = `${String(received)} of ${String(total)} requested documents arrived`;
  return (
    <View style={styles.progress}>
      <Text style={[styles.text, ink]}>{label}</Text>
      <Meter.Root value={received} min={0} max={Math.max(total, 1)} aria-label={label}>
        <Meter.Track>
          <Meter.Indicator />
        </Meter.Track>
      </Meter.Root>
      <Text style={[styles.caption, muted]}>
        {outstanding === 0 ? 'Nothing outstanding.' : `${String(outstanding)} outstanding.`}
        {waived > 0 ? ` ${String(waived)} not needed.` : ''}
      </Text>
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
    gap: spacing.md,
    padding: spacing.md,
  },
  caption: {
    fontSize: fontSizes.caption,
    lineHeight: fontSizes.caption * 1.5,
  },
  form: {
    gap: spacing.md,
  },
  head: {
    borderBottomWidth: StyleSheet.hairlineWidth,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
  },
  help: {
    fontSize: fontSizes.caption,
    paddingHorizontal: spacing.md,
    paddingTop: spacing.xs,
  },
  list: {
    gap: spacing.sm,
  },
  progress: {
    gap: spacing.xs,
  },
  row: {
    borderTopWidth: StyleSheet.hairlineWidth,
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
    justifyContent: 'space-between',
    paddingTop: spacing.sm,
  },
  rowActions: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.xs,
  },
  rowBody: {
    flexGrow: 1,
    flexShrink: 1,
    gap: spacing.xs,
    minWidth: 180,
  },
  rowMeta: {
    alignItems: 'center',
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
  },
  rowTitle: {
    fontSize: fontSizes.body,
    fontWeight: '600',
  },
  section: {
    borderWidth: StyleSheet.hairlineWidth,
    overflow: 'hidden',
  },
  text: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
});
