import type { CaseForm, CaseProblem, Note } from '@insolvia-ai/api-client';
import { Badge, Button } from '@insolvia-ai/design-system';
import type { BadgeIntent } from '@insolvia-ai/design-system';
import { useRouter } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { CaseColumn } from '@/components/case-shell';
import { Heading } from '@/components/heading';
import { NotesPanel } from '@/components/notes-panel';
import {
  DEFAULT_OUTPUT_OPTIONS,
  OutputOptionsPanel,
  outputOptionsRequestFrom,
  type OutputOptionsValue,
} from '@/components/output-options-panel';
import { openDownload } from '@/screens/documents/browser';
// NOT an assets/exemptions/means-test screen edit — `formatMoney` is a
// read-only import of a helper those screens already export and share
// (screens/intake/assets.tsx and screens/intake/exemptions.tsx do the same).
import { formatMoney } from '@/screens/intake/liens';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * Where a hub row's "Open" link goes — the data-entry screen that holds the
 * form's records, per the issue's own mapping. `undefined` means the form has
 * no data-entry screen of its own: B106Sum and the declaration are printed
 * from the OTHER schedules (case-data-model.md's "Derived values" table and
 * the declaration's signature block), so there is nothing to open.
 *
 * Every intake-backed row opens the SAME `/intake` screen — the section
 * picker there is a preparer's own next click, not a query the hub decides
 * for them (issue 8.5's screen has no section deep-link seam yet).
 */
const OPEN_SEGMENT: Readonly<Record<string, string>> = {
  'form/b101': 'petition',
  'form/b106ab': 'intake',
  'form/b106c': 'intake',
  'form/b106d': 'intake',
  'form/b106ef': 'intake',
  'form/b106g': 'intake',
  'form/b106h': 'intake',
  'form/b107': 'intake',
  'form/b106i': 'income',
  'form/b106j': 'income',
  'form/b106j2': 'income',
  'form/b122a1': 'means-test',
  'form/b122a2': 'means-test',
};

/** The gate's `source` in a person's words — the packet screen's own list,
 * widened with the sources the forms hub's grouping can also report. */
function describeSource(source: string): string {
  if (source.startsWith('form/')) {
    return `Form ${source.slice('form/'.length).toUpperCase()}`;
  }
  const labels: Record<string, string> = {
    case: 'Case',
    debtors: 'Debtors',
    petitions: 'Petition',
    sofa_entries: 'Financial affairs',
    creditors: 'Creditors',
    claims: 'Claims',
    exemptions: 'Exemptions',
    income_summaries: 'Income',
    employments: 'Employment',
    pay_period_records: 'Pay history',
    other_income_records: 'Other income',
    means_test_inputs: 'Means test',
    households: 'Households',
    expenses: 'Expenses',
    dependents: 'Dependents',
    codebtors: 'Codebtors',
  };
  return labels[source] ?? source.replace(/_/g, ' ');
}

/** The row's one number, in a person's words — an item count or a dollar
 * total, exactly as `core/forms_hub.py`'s two rules define it. */
function describeMetric(metric: CaseForm['metric']): string | null {
  if (metric === undefined) return null;
  if (metric.kind === 'total') return formatMoney(metric.value);
  const n = Number(metric.value);
  return `${metric.value} ${n === 1 ? 'item' : 'items'}`;
}

function statusBadge(problems: readonly CaseProblem[]): { intent: BadgeIntent; label: string } {
  if (problems.length === 0) return { intent: 'success', label: 'Complete' };
  return {
    intent: 'warning',
    label: `${problems.length} ${problems.length === 1 ? 'issue' : 'issues'}`,
  };
}

type ListState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly forms: readonly CaseForm[] }
  | { readonly kind: 'error'; readonly message: string };

/**
 * `/cases/<id>/forms` — the forms hub (issue 13.2 / #343).
 *
 * The packet screen renders the whole Chapter 7 set or nothing; this renders
 * one form at a time. Each row is a form `packet_form_series` says this
 * case's chapter and pin require, with its item count or dollar total (where
 * the server defines one for it) and the completeness problems that belong
 * to it — grouped server-side, never re-derived here (ADR 0001).
 *
 * "Preview" renders that ONE form through the same fill engine the packet
 * uses and opens the PDF — synchronously, no job to poll, because a
 * single-form render is fast enough for a request/response round trip
 * (`core/form_fill.py`'s own reasoning). "Open" goes to the screen that
 * collects the form's underlying records, so a preparer fixing what a row
 * flags lands on the right screen rather than back at the case overview.
 */
export function FormsHub({ caseId }: { readonly caseId: string }) {
  const theme = useTheme();
  const router = useRouter();
  const { call } = useApi();

  const [list, setList] = useState<ListState>({ kind: 'loading' });
  const [busySeries, setBusySeries] = useState<string | null>(null);
  const [activity, setActivity] = useState('');
  const [actionError, setActionError] = useState<string | null>(null);
  // The case's whole note list (issue 14.5 / #357), read ONCE here rather
  // than once per row: `NotesPanel` never fetches for itself (see its own
  // docstring), and thirteen rows each calling listNotes for their own count
  // would be thirteen requests for the answer one already gives.
  const [notes, setNotes] = useState<readonly Note[]>([]);
  // Which rows' note panels are open. A Set rather than one `string | null`
  // — a preparer comparing two schedules' notes side by side should be able
  // to expand both, the same way two browser tabs would let them.
  const [expandedNotes, setExpandedNotes] = useState<ReadonlySet<string>>(new Set());
  // Output options (issue 13.11): one panel for the whole screen, applied to
  // whichever row's "Preview" is next pressed — the same options a preview
  // renders with are exactly what a subsequent packet assembly would use for
  // that form, so one shared control is truer to the pipeline than a
  // per-row copy of the same four settings.
  const [options, setOptions] = useState<OutputOptionsValue>(DEFAULT_OUTPUT_OPTIONS);

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.listCaseForms(caseId));
      if (result.ok) {
        setList({ kind: 'ready', forms: result.value });
      }
    } catch {
      setList({ kind: 'error', message: 'Could not load this case’s forms.' });
    }
  }, [call, caseId]);

  const loadNotes = useCallback(async () => {
    try {
      const result = await call((client) => client.listNotes(caseId));
      if (result.ok) setNotes(result.value);
    } catch {
      // A row's note count and panel are a nicety next to the schedule
      // itself; a failed read just leaves them showing what they last had,
      // the same trade every other read on this screen makes.
    }
  }, [call, caseId]);

  useEffect(() => {
    void load();
    void loadNotes();
  }, [load, loadNotes]);

  const toggleNotes = (series: string) => {
    setExpandedNotes((current) => {
      const next = new Set(current);
      if (next.has(series)) {
        next.delete(series);
      } else {
        next.add(series);
      }
      return next;
    });
  };

  const preview = async (form: CaseForm) => {
    setActionError(null);
    setActivity('');
    setBusySeries(form.series);
    try {
      const result = await call((client) =>
        client.getCaseFormPreview(caseId, form.form, outputOptionsRequestFrom(options)),
      );
      if (result.ok) {
        if (result.value.problems.length > 0) {
          setActionError(
            `${form.title} is not ready to preview: ` +
              result.value.problems.map((problem) => problem.message).join(' '),
          );
        } else if (result.value.url !== undefined) {
          if (openDownload(result.value.url, `${form.form}.pdf`)) {
            setActivity(`Opened ${form.title}.`);
          } else {
            setActionError(`Could not open ${form.title} — this needs a web browser.`);
          }
        }
      }
    } catch {
      setActionError(`Could not render ${form.title}. Please try again.`);
    } finally {
      setBusySeries((current) => (current === form.series ? null : current));
    }
  };

  const openScreen = (form: CaseForm) => {
    const segment = OPEN_SEGMENT[form.series];
    if (segment === undefined) return;
    router.push(segment === '' ? `/cases/${caseId}` : `/cases/${caseId}/${segment}`);
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const danger = { color: theme.colors.danger, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  return (
    <CaseColumn>
      <Heading level={1}>Forms</Heading>
      <Text style={[styles.body, muted]}>
        Every form this case's chapter requires, one at a time: how complete each is, what each
        totals, and a PDF preview of exactly what would print — without assembling the whole filing
        packet.
      </Text>

      <OutputOptionsPanel value={options} onChange={setOptions} disabled={busySeries !== null} />

      <Text aria-live="polite" style={[styles.status, muted]}>
        {activity}
      </Text>
      <Text aria-live="assertive" style={[styles.status, danger]}>
        {actionError ?? (list.kind === 'error' ? list.message : '')}
      </Text>

      {list.kind === 'loading' ? (
        <Text style={[styles.body, muted]}>Loading…</Text>
      ) : list.kind === 'error' ? (
        <Text style={[styles.body, muted]}>{list.message} Reload the page to try again.</Text>
      ) : (
        <View role="list" style={styles.list}>
          {list.forms.map((form) => {
            const badge = statusBadge(form.problems);
            const metric = describeMetric(form.metric);
            const segment = OPEN_SEGMENT[form.series];
            const formNotes = notes.filter((note) => note.form_series === form.series);
            const notesOpen = expandedNotes.has(form.series);
            return (
              <View role="listitem" key={form.series} style={styles.row}>
                <View style={styles.rowHeader}>
                  <Text style={[styles.rowTitle, ink]}>
                    {form.officialNumber !== '' ? `${form.officialNumber} — ` : ''}
                    {form.title !== '' ? form.title : form.series}
                  </Text>
                  <Badge intent={badge.intent} size="sm">
                    {badge.label}
                  </Badge>
                </View>
                {metric !== null ? <Text style={[styles.body, muted]}>{metric}</Text> : null}

                {form.problems.length > 0 ? (
                  <View role="list" style={styles.problems}>
                    {form.problems.map((problem, index) => (
                      <View role="listitem" key={`${problem.source}-${index}`}>
                        <Text style={[styles.problemText, muted]}>
                          <Text style={[styles.problemSource, ink]}>
                            {describeSource(problem.source)}:{' '}
                          </Text>
                          {problem.message}
                        </Text>
                      </View>
                    ))}
                  </View>
                ) : null}

                <View style={styles.actions}>
                  <Button
                    size="lg"
                    intent="secondary"
                    disabled={busySeries === form.series}
                    onPress={() => {
                      void preview(form);
                    }}
                    aria-label={`Preview ${form.title} as a PDF`}
                  >
                    {busySeries === form.series ? 'Rendering…' : 'Preview'}
                  </Button>
                  {segment !== undefined ? (
                    <Button
                      size="lg"
                      onPress={() => {
                        openScreen(form);
                      }}
                      aria-label={`Open the screen that collects ${form.title}`}
                    >
                      Open
                    </Button>
                  ) : null}
                  <Button
                    size="lg"
                    intent="secondary"
                    onPress={() => {
                      toggleNotes(form.series);
                    }}
                    aria-label={
                      notesOpen ? `Hide notes for ${form.title}` : `Show notes for ${form.title}`
                    }
                  >
                    {`Notes (${formNotes.length})`}
                  </Button>
                </View>

                {notesOpen ? (
                  <View style={styles.notes}>
                    <NotesPanel
                      caseId={caseId}
                      notes={formNotes}
                      formSeries={form.series}
                      onChanged={() => void loadNotes()}
                    />
                  </View>
                ) : null}
              </View>
            );
          })}
        </View>
      )}
    </CaseColumn>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    gap: spacing.sm,
    marginTop: spacing.sm,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  list: {
    gap: spacing.lg,
    marginTop: spacing.sm,
  },
  notes: {
    marginTop: spacing.sm,
  },
  problemSource: {
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
  problemText: {
    fontSize: fontSizes.label,
    lineHeight: fontSizes.label * 1.5,
  },
  problems: {
    gap: spacing.xs / 2,
    marginTop: spacing.xs,
  },
  row: {
    gap: spacing.xs,
  },
  rowHeader: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.sm,
    justifyContent: 'space-between',
  },
  rowTitle: {
    flexShrink: 1,
    fontSize: fontSizes.body,
    fontWeight: '600',
  },
  status: {
    fontSize: fontSizes.label,
    minHeight: fontSizes.label * 1.5,
  },
});
