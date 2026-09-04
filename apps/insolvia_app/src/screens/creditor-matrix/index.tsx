import type { CreditorMatrix, CreditorMatrixProblem } from '@insolvia-ai/api-client';
import { Button } from '@insolvia-ai/design-system';
import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { saveTextFile } from '@/screens/documents/browser';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * `/cases/<id>/creditor-matrix` — generate and save the court's creditor
 * mailing matrix (issue #282, the UI half of #94).
 *
 * The endpoint is synchronous and always answers 200 with ONE of two things
 * (never both, never a partial file — core/creditor_matrix.py owns that rule):
 *
 * - **the file** — `content` is the exact CRLF text of `creditor-matrix.txt`,
 *   saved on the spot via {@link saveTextFile}: the bytes are already here, so
 *   there is no presigned URL to mint and no second press to ask for.
 * - **the problems** — every reason there is no file yet, each naming the
 *   creditor record and the body field to fix. They are GROUPED PER CREDITOR,
 *   the packet screen's blocked-list shape: the list is the deliverable, and a
 *   preparer fixes the whole case in one pass rather than one toast at a time.
 *
 * Problems name creditors by RECORD ID, which a preparer cannot act on — so
 * the case's creditor list is fetched once on mount and each group is headed
 * by the creditor's name. A failed names load degrades the headings to
 * numbered creditors, never the screen: the messages still say what to fix.
 */

/** One creditor's problems under one heading, in the server's order. */
interface ProblemGroup {
  readonly heading: string;
  readonly problems: readonly CreditorMatrixProblem[];
}

type Generation =
  | { readonly phase: 'idle' }
  | { readonly phase: 'running' }
  | { readonly phase: 'generated'; readonly matrix: CreditorMatrix }
  | { readonly phase: 'problems'; readonly problems: readonly CreditorMatrixProblem[] };

/**
 * The matrix's `field` in a person's words — the same names the intake form
 * puts on its inputs, so "Address — ZIP code" here is the label to look for
 * there. `address` alone is the whole city-state-ZIP line.
 */
function describeField(field: string): string {
  const labels: Record<string, string> = {
    name: 'Name',
    address: 'Address',
    'address.line1': 'Address — street',
    'address.line2': 'Address — apartment, suite or unit',
    'address.city': 'Address — city',
    'address.state': 'Address — state',
    'address.postal_code': 'Address — ZIP code',
    creditors: 'Creditors',
  };
  return labels[field] ?? field.replace(/_/g, ' ');
}

/**
 * Problems grouped per creditor, headed by the creditor's name when the names
 * load produced one. Groups keep the server's order (alphabetical by name —
 * the order the matrix itself would print); the case-level problem, which
 * names no creditor, gets a group of its own under "This case".
 */
function groupProblems(
  problems: readonly CreditorMatrixProblem[],
  names: Readonly<Record<string, string>>,
): readonly ProblemGroup[] {
  const groups: { key: string; heading: string; problems: CreditorMatrixProblem[] }[] = [];
  let ordinal = 0;
  for (const problem of problems) {
    const key = problem.creditorId ?? 'case';
    const existing = groups.find((group) => group.key === key);
    if (existing !== undefined) {
      existing.problems.push(problem);
      continue;
    }
    let heading = 'This case';
    if (problem.creditorId !== undefined) {
      ordinal += 1;
      heading = names[problem.creditorId] ?? `Creditor ${ordinal}`;
    }
    groups.push({ key, heading, problems: [problem] });
  }
  return groups.map(({ heading, problems: grouped }) => ({ heading, problems: grouped }));
}

export function CreditorMatrixScreen({ caseId }: { readonly caseId: string }) {
  const theme = useTheme();
  const { call } = useApi();

  const [generation, setGeneration] = useState<Generation>({ phase: 'idle' });
  const [names, setNames] = useState<Readonly<Record<string, string>>>({});
  const [activity, setActivity] = useState('');
  const [actionError, setActionError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await call((client) => client.listCaseEntities(caseId, 'creditors'));
        if (!result.ok || cancelled) return;
        const loaded: Record<string, string> = {};
        for (const record of result.value) {
          const name = (record as { name?: unknown }).name;
          if (typeof name === 'string' && name !== '') {
            loaded[record.id] = name;
          }
        }
        setNames(loaded);
      } catch {
        // Only the group headings degrade — the problems themselves carry
        // everything a preparer needs, so a failed names load is not an error
        // this screen announces.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, caseId]);

  const generate = async () => {
    if (generation.phase === 'running') {
      return;
    }
    setActionError(null);
    setActivity('Generating the creditor matrix…');
    setGeneration({ phase: 'running' });
    try {
      const result = await call((client) => client.getCreditorMatrix(caseId));
      if (!result.ok) {
        setActivity('');
        setGeneration({ phase: 'idle' });
        return;
      }
      const matrix = result.value;
      if (matrix.content !== undefined) {
        setGeneration({ phase: 'generated', matrix });
        if (saveTextFile(matrix.content, matrix.fileName)) {
          setActivity(`Saved ${matrix.fileName}.`);
        } else {
          setActivity('');
          setActionError(`Could not save ${matrix.fileName} — this needs a web browser.`);
        }
        return;
      }
      setActivity('');
      setGeneration({ phase: 'problems', problems: matrix.problems });
    } catch {
      setActivity('');
      setActionError('Could not generate the matrix. Please try again.');
      setGeneration({ phase: 'idle' });
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const danger = { color: theme.colors.danger, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  return (
    <AppShell>
      <Heading level={1}>Creditor matrix</Heading>
      <Text style={[styles.body, muted]}>
        Generates the court’s creditor mailing list — a plain-text file, one block per creditor, in
        the format the clerk’s noticing system ingests. Generation first checks every creditor is
        mailable, and refuses with a list of what to fix rather than producing a matrix with a
        creditor missing.
      </Text>

      {/* One always-present live region per urgency — the packet screen's
          rule, for the packet screen's reason. */}
      <Text aria-live="polite" style={[styles.status, muted]}>
        {activity}
      </Text>
      <Text aria-live="assertive" style={[styles.status, danger]}>
        {actionError ?? ''}
      </Text>

      <View style={styles.actions}>
        <Button
          size="lg"
          onPress={() => void generate()}
          disabled={generation.phase === 'running'}
          aria-label="Generate the creditor matrix for this case"
        >
          {generation.phase === 'running' ? 'Generating…' : 'Generate the matrix'}
        </Button>
      </View>

      {generation.phase === 'generated' ? (
        <View>
          <Heading level={2}>The matrix is ready</Heading>
          <Text style={[styles.body, muted]}>
            {`${generation.matrix.fileName} — ${generation.matrix.creditorCount} ${
              generation.matrix.creditorCount === 1 ? 'creditor' : 'creditors'
            }${
              generation.matrix.duplicatesOmitted > 0
                ? `, ${generation.matrix.duplicatesOmitted} duplicate ${
                    generation.matrix.duplicatesOmitted === 1 ? 'block' : 'blocks'
                  } omitted`
                : ''
            }. Upload it in CM/ECF’s “Upload List of Creditors” step.`}
          </Text>
        </View>
      ) : null}

      {generation.phase === 'problems' ? (
        <View>
          <Heading level={2}>The creditor list is not ready</Heading>
          <Text style={[styles.body, muted]}>
            No file was produced — a missing entry on the matrix is a bankruptcy notice that never
            arrives. Fix each creditor below under Intake → Creditors, then generate again.
          </Text>
          <View role="list" style={styles.list}>
            {/* Grouped at render time, not at settle time, so headings pick
                up the names load even when generation finishes first. */}
            {groupProblems(generation.problems, names).map((group) => (
              <View role="listitem" key={group.heading} style={styles.group}>
                <Text style={[styles.groupHeading, ink]}>{group.heading}</Text>
                {group.problems.map((problem, index) => (
                  <View key={`${problem.field}-${index}`} style={styles.problem}>
                    <Text style={[styles.problemField, ink]}>{describeField(problem.field)}</Text>
                    <Text style={[styles.body, muted]}>{problem.message}</Text>
                  </View>
                ))}
              </View>
            ))}
          </View>
        </View>
      ) : null}
    </AppShell>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    marginTop: spacing.sm,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  group: {
    gap: spacing.xs,
  },
  groupHeading: {
    fontSize: fontSizes.body,
    fontWeight: '600',
  },
  list: {
    gap: spacing.md,
    marginTop: spacing.sm,
  },
  problem: {
    gap: spacing.xs / 2,
  },
  problemField: {
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
  status: {
    fontSize: fontSizes.label,
    minHeight: fontSizes.label * 1.5,
  },
});
