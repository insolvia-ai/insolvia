import type {
  CaseCollection,
  CaseEntity,
  CaseStandards,
  CaseSummary,
  Debtor,
} from '@insolvia-ai/api-client';
import { Button, Table, Tabs } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { CaseColumn, useCase } from '@/components/case-shell';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

import { CollectionEditor } from '../intake/collection-editor';
import { COLLECTION_SPECS } from '../intake/collections';
import { groupExpenseTotals } from './expense-groups';
import { computeScheduleILines, summarizePayRecordsByEmployer } from './income-math';

/**
 * `/cases/<id>/income` — manual paycheck entry, Schedule I, Schedule J
 * against the IRS Standards, and the I−J excess (issue #348).
 *
 * A DEDICATED SCREEN, not a section folded into `/intake`. Intake's section
 * picker is for the ten generic collections read and written one record at a
 * time (issue #249); this screen exists because the income-and-expenses
 * picture is more than any one collection's list — a preparer needs the raw
 * pay records, the per-employer average, Schedule I's own computed lines,
 * Schedule J's totals by group, the published IRS Standards beside the
 * health-care and transportation lines, and the income-minus-expenses excess,
 * all in view together. It still REUSES `CollectionEditor` and the specs in
 * `intake/collections.ts` for every raw record (employments, pay periods,
 * other income, the Schedule I and J entities) rather than a second set of
 * forms — this screen adds the computed and comparative views around them.
 *
 * EVERY COMPUTED FIGURE traces to one of two places: the server (the
 * excess banner reads `GET /v1/cases/{id}/summary`, the standards panel reads
 * `GET /v1/cases/{id}/standards`), or a pure, unit-tested module in this
 * folder (`income-math.ts`, `expense-groups.ts`, `money.ts`) that mirrors the
 * server's own arithmetic for a live, un-saved preview. Nothing here re-derives
 * money math ad hoc in a render function.
 */

type LoadState<T> =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly value: T }
  | { readonly kind: 'error' };

function useCaseRead<T>(
  load: () => Promise<T | undefined>,
  deps: readonly unknown[],
): LoadState<T> {
  const [state, setState] = useState<LoadState<T>>({ kind: 'loading' });
  useEffect(() => {
    let live = true;
    setState({ kind: 'loading' });
    (async () => {
      try {
        const value = await load();
        if (!live) return;
        setState(value === undefined ? { kind: 'loading' } : { kind: 'ready', value });
      } catch {
        if (live) setState({ kind: 'error' });
      }
    })();
    return () => {
      live = false;
    };
    // `deps` IS the dependency list — a caller-supplied array, not a literal
    // this hook can see through, so there is nothing for a static linter to
    // check here beyond what the callers already keep correct.
  }, deps);
  return state;
}

function useCaseEntities<C extends CaseCollection>(
  caseId: string,
  collection: C,
  refreshToken: number,
): LoadState<readonly CaseEntity<C>[]> {
  const { call } = useApi();
  return useCaseRead(async () => {
    const result = await call((client) => client.listCaseEntities(caseId, collection));
    return result.ok ? result.value : undefined;
  }, [call, caseId, collection, refreshToken]);
}

/** A debtor as this screen labels it: the filing role, plus a name when
 * intake has supplied one — the same join order the case rail uses. */
function debtorLabel(debtor: Debtor): string {
  const role =
    debtor.filing_role === 'debtor_1'
      ? 'Debtor 1'
      : debtor.filing_role === 'debtor_2'
        ? 'Debtor 2'
        : 'Non-filing spouse';
  const parts = [debtor.name?.given, debtor.name?.surname]
    .map((part) => part?.trim())
    .filter((part): part is string => Boolean(part));
  return parts.length > 0 ? `${role} — ${parts.join(' ')}` : role;
}

export interface IncomeWorkbenchProps {
  readonly caseId: string;
}

export function IncomeWorkbench({ caseId }: IncomeWorkbenchProps) {
  const theme = useTheme();
  const { call } = useApi();
  const { debtors } = useCase();
  const [tab, setTab] = useState<'income' | 'expenses'>('income');
  const [refreshToken, setRefreshToken] = useState(0);
  const refresh = useCallback(() => setRefreshToken((n) => n + 1), []);

  const summary = useCaseRead<CaseSummary>(async () => {
    const result = await call((client) => client.getCaseSummary(caseId));
    return result.ok ? result.value : undefined;
  }, [call, caseId, refreshToken]);

  const standards = useCaseRead<CaseStandards>(async () => {
    const result = await call((client) => client.getCaseStandards(caseId));
    return result.ok ? result.value : undefined;
  }, [call, caseId, refreshToken]);

  const employments = useCaseEntities(caseId, 'employments', refreshToken);
  const payPeriods = useCaseEntities(caseId, 'pay_period_records', refreshToken);
  const incomeSummaries = useCaseEntities(caseId, 'income_summaries', refreshToken);
  const expenses = useCaseEntities(caseId, 'expenses', refreshToken);

  const specFor = (collection: CaseCollection) =>
    COLLECTION_SPECS.find((candidate) => candidate.collection === collection);

  const employmentSpec = specFor('employments');
  const payPeriodSpec = specFor('pay_period_records');
  const otherIncomeSpec = specFor('other_income_records');
  const incomeSummarySpec = specFor('income_summaries');
  const householdSpec = specFor('households');
  const expenseSpec = specFor('expenses');
  const dependentSpec = specFor('dependents');

  return (
    <CaseColumn>
      <Heading level={1}>Income &amp; expenses</Heading>
      <Text
        style={[styles.intro, { color: theme.colors.muted, fontFamily: theme.typography.body }]}
      >
        Enter pay stubs and other income by hand, confirm Schedule I and J, and compare Schedule J
        against the IRS Standards — the same records the means test reads, whether they arrived by
        hand or by extraction.
      </Text>

      <ExcessBanner summary={summary} onRefresh={refresh} />

      <Tabs.Root
        defaultValue="income"
        value={tab}
        onValueChange={(next) => setTab(next as 'income' | 'expenses')}
        aria-label="Income or expenses"
      >
        <Tabs.List>
          <Tabs.Tab value="income">Income</Tabs.Tab>
          <Tabs.Tab value="expenses">Expenses</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="income">
          <Heading level={2}>Employers</Heading>
          {employmentSpec !== undefined ? (
            <CollectionEditor caseId={caseId} spec={employmentSpec} />
          ) : null}

          <Heading level={2}>Pay records</Heading>
          {payPeriodSpec !== undefined ? (
            <CollectionEditor caseId={caseId} spec={payPeriodSpec} />
          ) : null}

          <Heading level={3}>Per-employer monthly summary</Heading>
          <EmployerSummary employments={employments} payPeriods={payPeriods} />

          <Heading level={2}>Other income</Heading>
          {otherIncomeSpec !== undefined ? (
            <CollectionEditor caseId={caseId} spec={otherIncomeSpec} />
          ) : null}

          <Heading level={2}>Schedule I</Heading>
          {incomeSummarySpec !== undefined ? (
            <CollectionEditor caseId={caseId} spec={incomeSummarySpec} />
          ) : null}

          <Heading level={3}>Schedule I — computed lines</Heading>
          <ScheduleIPreview debtors={debtors} incomeSummaries={incomeSummaries} />
        </Tabs.Panel>

        <Tabs.Panel value="expenses">
          <Heading level={2}>Households</Heading>
          {householdSpec !== undefined ? (
            <CollectionEditor caseId={caseId} spec={householdSpec} />
          ) : null}

          <Heading level={2}>Dependents</Heading>
          {dependentSpec !== undefined ? (
            <CollectionEditor caseId={caseId} spec={dependentSpec} />
          ) : null}

          <Heading level={2}>Monthly expenses</Heading>
          {expenseSpec !== undefined ? (
            <CollectionEditor caseId={caseId} spec={expenseSpec} />
          ) : null}

          <Heading level={3}>Schedule J — by group, against the IRS Standards</Heading>
          <ScheduleJGroups expenses={expenses} standards={standards} />
        </Tabs.Panel>
      </Tabs.Root>
    </CaseColumn>
  );
}

/** One exact figure, rendered as the server sent it — no arithmetic here,
 * matching the case overview's own `Figure`. */
function MoneyFigure({ label, value }: { label: string; value: string | undefined }) {
  const theme = useTheme();
  return (
    <View style={styles.figure}>
      <Text
        style={[
          styles.figureLabel,
          { color: theme.colors.muted, fontFamily: theme.typography.body },
        ]}
      >
        {label}
      </Text>
      <Text
        style={[styles.figureValue, { color: theme.colors.ink, fontFamily: theme.typography.mono }]}
      >
        {value ?? '—'}
      </Text>
    </View>
  );
}

/**
 * The persistent income-minus-expenses figure (issue #348), always visible
 * regardless of which tab is open — the whole point is that a preparer never
 * has to leave one schedule to remember the other's total. `GET
 * /v1/cases/{id}/summary` is the one source; nothing here subtracts anything.
 */
function ExcessBanner({
  summary,
  onRefresh,
}: {
  summary: LoadState<CaseSummary>;
  onRefresh: () => void;
}) {
  const theme = useTheme();
  const totals = summary.kind === 'ready' ? summary.value.totals : undefined;
  return (
    <View style={[styles.banner, { borderColor: theme.colors.line }]}>
      <MoneyFigure label="Monthly income (106I line 12)" value={totals?.monthlyIncome} />
      <MoneyFigure label="Monthly expenses (106J line 22c)" value={totals?.monthlyExpenses} />
      <MoneyFigure label="Monthly excess" value={totals?.monthlyExcess} />
      <Button size="sm" onPress={onRefresh}>
        Refresh figures
      </Button>
      {summary.kind === 'error' ? (
        <Text
          aria-live="assertive"
          style={[styles.error, { color: theme.colors.danger, fontFamily: theme.typography.body }]}
        >
          Could not load the income and expense totals.
        </Text>
      ) : null}
    </View>
  );
}

function EmployerSummary({
  employments,
  payPeriods,
}: {
  employments: LoadState<readonly CaseEntity<'employments'>[]>;
  payPeriods: LoadState<readonly CaseEntity<'pay_period_records'>[]>;
}) {
  const theme = useTheme();
  if (employments.kind !== 'ready' || payPeriods.kind !== 'ready') {
    return (
      <Text style={[styles.note, { color: theme.colors.muted, fontFamily: theme.typography.body }]}>
        Loading pay records…
      </Text>
    );
  }
  const rows = summarizePayRecordsByEmployer(payPeriods.value);
  if (rows.length === 0) {
    return (
      <Text style={[styles.note, { color: theme.colors.muted, fontFamily: theme.typography.body }]}>
        No pay records entered yet.
      </Text>
    );
  }
  const nameFor = (employmentId: string) =>
    employments.value.find((employment) => employment.id === employmentId)?.employer_name ??
    'Unnamed employer';
  return (
    <Table.Root dense>
      <Table.Head>
        <Table.Row>
          <Table.HeaderCell>Employer</Table.HeaderCell>
          <Table.HeaderCell width={90}>Records</Table.HeaderCell>
          <Table.HeaderCell width={120}>Avg. gross</Table.HeaderCell>
          <Table.HeaderCell width={120}>Avg. net</Table.HeaderCell>
        </Table.Row>
      </Table.Head>
      <Table.Body>
        {rows.map((row) => (
          <Table.Row key={row.employmentId}>
            <Table.Cell>{nameFor(row.employmentId)}</Table.Cell>
            <Table.Cell width={90}>{String(row.recordCount)}</Table.Cell>
            <Table.Cell width={120}>{`$${row.averageGross}`}</Table.Cell>
            <Table.Cell width={120}>{`$${row.averageNet}`}</Table.Cell>
          </Table.Row>
        ))}
      </Table.Body>
    </Table.Root>
  );
}

function ScheduleIPreview({
  debtors,
  incomeSummaries,
}: {
  debtors: readonly Debtor[];
  incomeSummaries: LoadState<readonly CaseEntity<'income_summaries'>[]>;
}) {
  const theme = useTheme();
  if (incomeSummaries.kind !== 'ready') {
    return (
      <Text style={[styles.note, { color: theme.colors.muted, fontFamily: theme.typography.body }]}>
        Loading Schedule I…
      </Text>
    );
  }
  if (incomeSummaries.value.length === 0) {
    return (
      <Text style={[styles.note, { color: theme.colors.muted, fontFamily: theme.typography.body }]}>
        No Schedule I entries yet.
      </Text>
    );
  }
  return (
    <Table.Root dense>
      <Table.Head>
        <Table.Row>
          <Table.HeaderCell>Debtor</Table.HeaderCell>
          <Table.HeaderCell width={110}>Gross income</Table.HeaderCell>
          <Table.HeaderCell width={110}>Deductions</Table.HeaderCell>
          <Table.HeaderCell width={110}>Take-home</Table.HeaderCell>
          <Table.HeaderCell width={110}>Other income</Table.HeaderCell>
          <Table.HeaderCell width={130}>Monthly income</Table.HeaderCell>
        </Table.Row>
      </Table.Head>
      <Table.Body>
        {incomeSummaries.value.map((record) => {
          const lines = computeScheduleILines(record);
          const debtor = debtors.find((candidate) => candidate.id === record.debtor_id);
          return (
            <Table.Row key={record.id}>
              <Table.Cell>{debtor !== undefined ? debtorLabel(debtor) : 'Unassigned'}</Table.Cell>
              <Table.Cell width={110}>{`$${lines.grossIncome}`}</Table.Cell>
              <Table.Cell width={110}>{`$${lines.totalDeductions}`}</Table.Cell>
              <Table.Cell width={110}>{`$${lines.takeHomePay}`}</Table.Cell>
              <Table.Cell width={110}>{`$${lines.totalOtherIncome}`}</Table.Cell>
              <Table.Cell width={130}>{`$${lines.monthlyIncome}`}</Table.Cell>
            </Table.Row>
          );
        })}
      </Table.Body>
    </Table.Root>
  );
}

/** Which standards figure, if any, a group's row shows beside it — issue
 * #348's "beside the health-care and transportation lines". */
function standardsNote(groupName: string, standards: LoadState<CaseStandards>): string | null {
  if (standards.kind !== 'ready') return null;
  const { nationalStandards, localStandards, problems } = standards.value;
  if (groupName === 'Health care') {
    if (nationalStandards.allowance === null) return null;
    return `IRS out-of-pocket allowance: $${nationalStandards.oopHealthcareUnder65 ?? '—'} per person under 65`;
  }
  if (groupName === 'Transportation') {
    if (localStandards.transportationOperatingOneCar === null) {
      return problems.length > 0 ? (problems[0] ?? null) : null;
    }
    return `IRS one-vehicle operating standard: $${localStandards.transportationOperatingOneCar}`;
  }
  return null;
}

function ScheduleJGroups({
  expenses,
  standards,
}: {
  expenses: LoadState<readonly CaseEntity<'expenses'>[]>;
  standards: LoadState<CaseStandards>;
}) {
  const theme = useTheme();
  if (expenses.kind !== 'ready') {
    return (
      <Text style={[styles.note, { color: theme.colors.muted, fontFamily: theme.typography.body }]}>
        Loading Schedule J…
      </Text>
    );
  }
  const groups = groupExpenseTotals(expenses.value);
  return (
    <>
      <Table.Root dense>
        <Table.Head>
          <Table.Row>
            <Table.HeaderCell>Group</Table.HeaderCell>
            <Table.HeaderCell width={90}>Lines</Table.HeaderCell>
            <Table.HeaderCell width={120}>Monthly total</Table.HeaderCell>
          </Table.Row>
        </Table.Head>
        <Table.Body>
          {groups.map((group) => {
            const note = standardsNote(group.name, standards);
            return (
              <Table.Row key={group.name}>
                <Table.Cell>
                  <Text style={{ color: theme.colors.ink, fontFamily: theme.typography.body }}>
                    {group.name}
                  </Text>
                  {note !== null ? (
                    <Text
                      style={[
                        styles.note,
                        { color: theme.colors.muted, fontFamily: theme.typography.body },
                      ]}
                    >
                      {note}
                    </Text>
                  ) : null}
                </Table.Cell>
                <Table.Cell width={90}>{String(group.recordCount)}</Table.Cell>
                <Table.Cell width={120}>{`$${group.total}`}</Table.Cell>
              </Table.Row>
            );
          })}
        </Table.Body>
      </Table.Root>
      {standards.kind === 'ready' && standards.value.problems.length > 0 ? (
        <Text
          aria-live="assertive"
          style={[styles.note, { color: theme.colors.muted, fontFamily: theme.typography.body }]}
        >
          {standards.value.problems.join(' ')}
        </Text>
      ) : null}
    </>
  );
}

const styles = StyleSheet.create({
  intro: {
    fontSize: fontSizes.label,
    lineHeight: fontSizes.label * 1.5,
    marginBottom: spacing.md,
  },
  banner: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'flex-end',
    gap: spacing.lg,
    borderWidth: 1,
    borderRadius: 8,
    padding: spacing.md,
    marginBottom: spacing.lg,
  },
  figure: { minWidth: 160 },
  figureLabel: { fontSize: fontSizes.label },
  figureValue: { fontSize: fontSizes.section, marginTop: spacing.xs },
  note: { fontSize: fontSizes.label, marginTop: spacing.xs },
  error: { fontSize: fontSizes.label, marginTop: spacing.xs },
});
