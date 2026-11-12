import {
  ApiValidationException,
  PLAN_PAYMENT_SOURCES,
  SECURED_TREATMENTS,
  UNSECURED_TREATMENTS,
  staffTypedProvenance,
} from '@insolvia-ai/api-client';
import type {
  CaseEntity,
  CaseEntityRequest,
  PlanBody,
  PlanCalculation,
  PlanPaymentSource,
  PlanScenario,
  SecuredTreatmentKind,
  UnsecuredTreatment,
} from '@insolvia-ai/api-client';
import { Badge, Button, Field, Input, Select, Table } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { bodyErrors } from '@/api/body-errors';
import { useApi } from '@/api/use-api';
import { CaseColumn, useCase } from '@/components/case-shell';
import { Heading } from '@/components/heading';
import { MoneyInput, moneyOf } from '@/components/money-input';
import { newRowId } from '@/screens/intake/row-id';
import { fontSizes, spacing, useTheme } from '@/theme';

import {
  TREATMENT_FIELDS,
  TREATMENT_LABELS,
  bestInterestsVerdict,
  blankToAbsent,
  feasibilityVerdict,
  moneyText,
  monthsText,
  percentText,
  treatmentFor,
  wholeNumberOf,
  withList,
  withTreatment,
} from './plan-body';
import type { Verdict } from './plan-body';

/**
 * `/cases/<id>/plan` — the Chapter 13 plan (issue 16.2 / #366): the proposal
 * on the left of every decision, the waterfall and the two tests the server
 * computes from it.
 *
 * THE FIGURES ARE THE SERVER'S. `GET /v1/cases/{id}/plan-calculation` runs
 * the calculator over the stored plan and the case's claims, schedules and
 * means test; this screen renders it and adds nothing up (ADR 0001). Every
 * save re-reads it — "live" means recomputed where the rules live, after
 * each confirmed change, the means-test screen's pattern.
 *
 * TWO KINDS OF CHANGE, KEPT APART. The proposal is ONE `plans` record,
 * saved whole with staff_typed provenance by one explicit Save. Comparing an
 * alternative is not a change at all: it POSTs an unsaved scenario and shows
 * the result beside the current one, and "Use this alternative" only copies
 * it into the unsaved form — the preparer still confirms it with Save, which
 * is the confirm-before-entry rule applied to a what-if.
 */

type LoadState<T> =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly value: T }
  | { readonly kind: 'error' };

const PAYMENT_SOURCE_OPTIONS = PLAN_PAYMENT_SOURCES.map((value) => ({
  value,
  label: {
    fixed: 'A typed monthly amount',
    schedule_j_excess: 'Schedule J net income (line 23c)',
    disposable_income: 'Means-test disposable income (122C-2 line 45)',
  }[value],
}));

const TREATMENT_OPTIONS = SECURED_TREATMENTS.map((value) => ({
  value,
  label: TREATMENT_LABELS[value],
}));

const UNSECURED_OPTIONS = UNSECURED_TREATMENTS.map((value) => ({
  value,
  label: {
    pot: 'Whatever remains (a pot plan)',
    percentage: 'A promised percentage',
    amount: 'A promised amount',
  }[value],
}));

type ClaimEntity = CaseEntity<'claims'>;

export interface PlanProps {
  readonly caseId: string;
}

export function Plan({ caseId }: PlanProps) {
  const theme = useTheme();
  const { call } = useApi();
  const { matter } = useCase();

  const [calculation, setCalculation] = useState<LoadState<PlanCalculation>>({
    kind: 'loading',
  });
  const [planId, setPlanId] = useState<string | null>(null);
  const [body, setBody] = useState<PlanBody>({});
  const [loaded, setLoaded] = useState(false);
  const [claims, setClaims] = useState<readonly ClaimEntity[]>([]);
  const [creditorNames, setCreditorNames] = useState<ReadonlyMap<string, string>>(new Map());
  const [status, setStatus] = useState('');
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [saving, setSaving] = useState(false);

  const loadCalculation = useCallback(async () => {
    try {
      const result = await call((client) => client.getCasePlanCalculation(caseId));
      if (result.ok) setCalculation({ kind: 'ready', value: result.value });
    } catch {
      setCalculation({ kind: 'error' });
    }
  }, [call, caseId]);

  useEffect(() => {
    void loadCalculation();
  }, [loadCalculation]);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const plans = await call((client) => client.listCaseEntities(caseId, 'plans'));
        if (!plans.ok || cancelled) return;
        // ONE per case by meaning; the first record is the one edited here,
        // and the one the calculator reads.
        const first = plans.value[0];
        if (first !== undefined) {
          setPlanId(first.id);
          setBody(first.body);
        }
        setLoaded(true);
      } catch {
        if (!cancelled) setStatus('Could not load the plan.');
      }
      try {
        const [claimList, creditorList] = await Promise.all([
          call((client) => client.listCaseEntities(caseId, 'claims')),
          call((client) => client.listCaseEntities(caseId, 'creditors')),
        ]);
        if (cancelled) return;
        if (claimList.ok) {
          setClaims(claimList.value.filter((claim) => claim.body.claimClass === 'secured'));
        }
        if (creditorList.ok) {
          setCreditorNames(
            new Map(creditorList.value.map((creditor) => [creditor.id, creditor.body.name ?? ''])),
          );
        }
      } catch {
        // The treatment rows need the claims; the waterfall does not.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, caseId]);

  const persist = async () => {
    setSaving(true);
    setStatus('Saving…');
    try {
      const request: CaseEntityRequest<'plans'> = {
        body,
        provenance: staffTypedProvenance(body),
      };
      const result = await call((client) =>
        planId === null
          ? client.addCaseEntity(caseId, 'plans', request)
          : client.putCaseEntity(caseId, 'plans', planId, request),
      );
      if (!result.ok) {
        setStatus('');
        return;
      }
      setPlanId(result.value.id);
      setBody(result.value.body);
      setErrors({});
      setStatus('Saved — recalculating');
      await loadCalculation();
      setStatus('Saved');
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setErrors(bodyErrors(cause.fields));
        setStatus('Some answers need attention.');
      } else {
        setStatus('Could not save. Your entries are still here — try again.');
      }
    } finally {
      setSaving(false);
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ready = calculation.kind === 'ready' ? calculation.value : null;

  const set = <K extends keyof PlanBody>(key: K, value: PlanBody[K]) =>
    setBody((current) => {
      if (value === undefined) {
        const { [key]: _removed, ...without } = current;
        return without;
      }
      return { ...current, [key]: value };
    });

  const saveButton = (
    <Button size="lg" disabled={saving || !loaded} onPress={() => void persist()}>
      Save and recalculate
    </Button>
  );

  return (
    <CaseColumn>
      <Heading level={1}>Chapter 13 plan</Heading>
      <Text style={[styles.intro, muted]}>
        The payment waterfall — trustee, ongoing payments, secured claims, attorney’s fees, priority
        claims, then general unsecured creditors — with feasibility and the § 1325(a)(4) liquidation
        test, computed by the server from this plan and the case’s schedules. Every figure names the
        claim, entry or rule it came from.
      </Text>
      {matter.chapter === 13 ? null : (
        <Text style={[styles.note, muted]}>
          {`This is a Chapter ${matter.chapter} case; a plan is proposed only under Chapter 13.`}
        </Text>
      )}

      <SummaryBanner calculation={calculation} />
      <View style={styles.actions}>
        {saveButton}
        <Text aria-live="polite" style={[styles.status, muted]}>
          {status}
        </Text>
      </View>

      <Section title="Payments">
        <TextField
          label="Term in months (blank: the commitment period)"
          value={body.termMonths === undefined ? '' : String(body.termMonths)}
          error={errors.termMonths}
          description={
            ready?.commitmentPeriod.months == null
              ? 'The means test has not set a commitment period yet.'
              : `The means test sets ${ready.commitmentPeriod.months} months — ${ready.commitmentPeriod.source ?? ''}.`
          }
          onValueChange={(next) => set('termMonths', wholeNumberOf(next))}
        />
        <Field.Root invalid={Boolean(errors.paymentSource)}>
          <Field.Label>Where the monthly payment comes from</Field.Label>
          <Select
            options={PAYMENT_SOURCE_OPTIONS}
            value={body.paymentSource ?? null}
            onValueChange={(next) =>
              set(
                'paymentSource',
                PLAN_PAYMENT_SOURCES.includes(next as PlanPaymentSource)
                  ? (next as PlanPaymentSource)
                  : undefined,
              )
            }
            placeholder="Choose a source"
          />
          {ready?.funding.baseSource == null ? null : (
            <Field.Description>
              {`Currently ${moneyText(ready.funding.basePayment)} a month — ${ready.funding.baseSource}.`}
            </Field.Description>
          )}
          {errors.paymentSource ? <Field.Error match>{errors.paymentSource}</Field.Error> : null}
        </Field.Root>
        {body.paymentSource === undefined || body.paymentSource === 'fixed' ? (
          <MoneyField
            label="Monthly payment"
            value={body.monthlyPayment}
            error={errors.monthlyPayment}
            onValueChange={(next) => set('monthlyPayment', next)}
          />
        ) : null}
        <MoneyField
          label="Trustee’s percentage fee (at most 10%)"
          value={body.trusteePercentage}
          error={errors.trusteePercentage}
          onValueChange={(next) => set('trusteePercentage', next)}
        />
        <MoneyField
          label="Attorney’s fees paid through the plan"
          value={body.attorneyFees}
          error={errors.attorneyFees}
          onValueChange={(next) => set('attorneyFees', next)}
        />
        <MoneyField
          label="Attorney’s fees, most paid in one month (blank: whatever is available)"
          value={body.attorneyFeeMonthly}
          error={errors.attorneyFeeMonthly}
          onValueChange={(next) => set('attorneyFeeMonthly', next)}
        />

        <Heading level={3} size="body">
          Step payments
        </Heading>
        {(body.stepPayments ?? []).map((step, index) => (
          <View key={step.id} style={styles.rowGroup}>
            <TextField
              label={`Step ${index + 1}: starts in month`}
              value={step.startMonth === undefined ? '' : String(step.startMonth)}
              error={errors[`stepPayments[${index}].startMonth`]}
              onValueChange={(next) =>
                set(
                  'stepPayments',
                  (body.stepPayments ?? []).map((row) =>
                    row.id === step.id ? { ...row, startMonth: wholeNumberOf(next) } : row,
                  ),
                )
              }
            />
            <MoneyField
              label={`Step ${index + 1}: monthly payment from then on`}
              value={step.monthlyPayment}
              error={errors[`stepPayments[${index}].monthlyPayment`]}
              onValueChange={(next) =>
                set(
                  'stepPayments',
                  (body.stepPayments ?? []).map((row) =>
                    row.id === step.id ? { ...row, monthlyPayment: next } : row,
                  ),
                )
              }
            />
            <Button
              size="lg"
              intent="secondary"
              onPress={() =>
                setBody((current) =>
                  withList(
                    current,
                    'stepPayments',
                    (current.stepPayments ?? []).filter((row) => row.id !== step.id),
                  ),
                )
              }
            >
              {`Remove step ${index + 1}`}
            </Button>
          </View>
        ))}
        <Button
          size="lg"
          intent="secondary"
          onPress={() =>
            setBody((current) => ({
              ...current,
              stepPayments: [...(current.stepPayments ?? []), { id: newRowId() }],
            }))
          }
        >
          Add a step payment
        </Button>

        <Heading level={3} size="body">
          Lump sums
        </Heading>
        {(body.lumpSums ?? []).map((lump, index) => (
          <View key={lump.id} style={styles.rowGroup}>
            <TextField
              label={`Lump sum ${index + 1}: month received`}
              value={lump.month === undefined ? '' : String(lump.month)}
              error={errors[`lumpSums[${index}].month`]}
              onValueChange={(next) =>
                set(
                  'lumpSums',
                  (body.lumpSums ?? []).map((row) =>
                    row.id === lump.id ? { ...row, month: wholeNumberOf(next) } : row,
                  ),
                )
              }
            />
            <MoneyField
              label={`Lump sum ${index + 1}: amount`}
              value={lump.amount}
              error={errors[`lumpSums[${index}].amount`]}
              onValueChange={(next) =>
                set(
                  'lumpSums',
                  (body.lumpSums ?? []).map((row) =>
                    row.id === lump.id ? { ...row, amount: next } : row,
                  ),
                )
              }
            />
            <TextField
              label={`Lump sum ${index + 1}: source`}
              value={lump.description ?? ''}
              error={errors[`lumpSums[${index}].description`]}
              onValueChange={(next) =>
                set(
                  'lumpSums',
                  (body.lumpSums ?? []).map((row) =>
                    row.id === lump.id ? { ...row, description: blankToAbsent(next) } : row,
                  ),
                )
              }
            />
            <Button
              size="lg"
              intent="secondary"
              onPress={() =>
                setBody((current) =>
                  withList(
                    current,
                    'lumpSums',
                    (current.lumpSums ?? []).filter((row) => row.id !== lump.id),
                  ),
                )
              }
            >
              {`Remove lump sum ${index + 1}`}
            </Button>
          </View>
        ))}
        <Button
          size="lg"
          intent="secondary"
          onPress={() =>
            setBody((current) => ({
              ...current,
              lumpSums: [...(current.lumpSums ?? []), { id: newRowId() }],
            }))
          }
        >
          Add a lump sum
        </Button>
      </Section>

      <Section title="Secured claims">
        {claims.length === 0 ? (
          <Text style={[styles.note, muted]}>This case has no secured claims.</Text>
        ) : null}
        {claims.map((claim) => {
          const name = `${creditorNames.get(claim.body.creditorId ?? '') || 'Unnamed creditor'}${
            claim.body.accountLast4 ? ` (…${claim.body.accountLast4})` : ''
          }`;
          const treatment = treatmentFor(body, claim.id);
          const index = (body.securedTreatments ?? []).findIndex((row) => row.claimId === claim.id);
          const errorFor = (key: string) =>
            index < 0 ? undefined : errors[`securedTreatments[${index}].${key}`];
          return (
            <View key={claim.id} style={styles.rowGroup}>
              <Heading level={3} size="body">
                {`${name} — ${moneyText(claim.body.amount === undefined ? null : String(claim.body.amount))}`}
              </Heading>
              <Field.Root invalid={Boolean(errorFor('treatment'))}>
                <Field.Label>{`Treatment of ${name}`}</Field.Label>
                <Select
                  options={TREATMENT_OPTIONS}
                  value={treatment?.treatment ?? null}
                  onValueChange={(next) =>
                    setBody((current) =>
                      withTreatment(
                        current,
                        claim.id,
                        {
                          treatment: SECURED_TREATMENTS.includes(next as SecuredTreatmentKind)
                            ? (next as SecuredTreatmentKind)
                            : undefined,
                        },
                        newRowId,
                      ),
                    )
                  }
                  placeholder="No treatment chosen"
                />
                {errorFor('treatment') ? (
                  <Field.Error match>{errorFor('treatment')}</Field.Error>
                ) : null}
              </Field.Root>
              {treatment?.treatment === undefined
                ? null
                : TREATMENT_FIELDS[treatment.treatment].map((field) => (
                    <MoneyField
                      key={field.key}
                      label={`${name}: ${field.label}`}
                      value={treatment[field.key]}
                      error={errorFor(field.key)}
                      onValueChange={(next) =>
                        setBody((current) =>
                          withTreatment(current, claim.id, { [field.key]: next }, newRowId),
                        )
                      }
                    />
                  ))}
            </View>
          );
        })}
      </Section>

      <Section title="Priority and unsecured claims">
        <MoneyField
          label="Share of each priority claim paid (blank: 100%)"
          value={body.priorityPercentage}
          error={errors.priorityPercentage}
          onValueChange={(next) => set('priorityPercentage', next)}
        />
        <MoneyField
          label="Interest on priority claims (% a year, blank: none)"
          value={body.priorityInterestRate}
          error={errors.priorityInterestRate}
          onValueChange={(next) => set('priorityInterestRate', next)}
        />
        <Field.Root invalid={Boolean(errors.unsecuredTreatment)}>
          <Field.Label>What general unsecured creditors receive</Field.Label>
          <Select
            options={UNSECURED_OPTIONS}
            value={body.unsecuredTreatment ?? null}
            onValueChange={(next) =>
              set(
                'unsecuredTreatment',
                UNSECURED_TREATMENTS.includes(next as UnsecuredTreatment)
                  ? (next as UnsecuredTreatment)
                  : undefined,
              )
            }
            placeholder="Whatever remains (a pot plan)"
          />
          {errors.unsecuredTreatment ? (
            <Field.Error match>{errors.unsecuredTreatment}</Field.Error>
          ) : null}
        </Field.Root>
        {body.unsecuredTreatment === 'percentage' ? (
          <MoneyField
            label="Promised percentage of unsecured claims"
            value={body.unsecuredPercentage}
            error={errors.unsecuredPercentage}
            onValueChange={(next) => set('unsecuredPercentage', next)}
          />
        ) : null}
        {body.unsecuredTreatment === 'amount' ? (
          <MoneyField
            label="Promised amount to unsecured creditors"
            value={body.unsecuredAmount}
            error={errors.unsecuredAmount}
            onValueChange={(next) => set('unsecuredAmount', next)}
          />
        ) : null}
        <MoneyField
          label="Interest on unsecured claims (% a year, blank: none)"
          value={body.unsecuredInterestRate}
          error={errors.unsecuredInterestRate}
          onValueChange={(next) => set('unsecuredInterestRate', next)}
        />
      </Section>

      <Section title="The liquidation comparison (§ 1325(a)(4))">
        <MoneyField
          label="Other Chapter 7 costs (sale costs, beyond the trustee’s commission)"
          value={body.chapter7OtherCosts}
          error={errors.chapter7OtherCosts}
          onValueChange={(next) => set('chapter7OtherCosts', next)}
        />
        <TextField
          label="What those costs are"
          value={body.chapter7OtherCostsDescription ?? ''}
          error={errors.chapter7OtherCostsDescription}
          onValueChange={(next) => set('chapter7OtherCostsDescription', blankToAbsent(next))}
        />
        <MoneyField
          label="Present-value discount rate (% a year, blank: compare nominally)"
          value={body.presentValueRate}
          error={errors.presentValueRate}
          onValueChange={(next) => set('presentValueRate', next)}
        />
        {ready === null ? null : <LiquidationTable calculation={ready} />}
      </Section>

      <View style={styles.actions}>{saveButton}</View>

      <Section title="The waterfall">
        {ready === null ? (
          <Text style={[styles.note, muted]}>Waiting for the calculation.</Text>
        ) : (
          <Waterfall calculation={ready} />
        )}
      </Section>

      <Section title="Compare an alternative">
        <ScenarioPanel
          caseId={caseId}
          body={body}
          current={ready}
          onAdopt={(next) => {
            setBody(next);
            setStatus('Alternative copied into the plan — not saved yet.');
          }}
        />
      </Section>
    </CaseColumn>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <View style={styles.section}>
      <Heading level={2} size="section">
        {title}
      </Heading>
      {children}
    </View>
  );
}

function TextField({
  label,
  value,
  error,
  description,
  onValueChange,
}: {
  readonly label: string;
  readonly value: string;
  readonly error: string | undefined;
  readonly description?: string | undefined;
  readonly onValueChange: (next: string) => void;
}) {
  return (
    <Field.Root invalid={Boolean(error)}>
      <Field.Label>{label}</Field.Label>
      <Input value={value} onValueChange={onValueChange} autoCorrect={false} />
      {description !== undefined ? <Field.Description>{description}</Field.Description> : null}
      {error ? <Field.Error match>{error}</Field.Error> : null}
    </Field.Root>
  );
}

function MoneyField({
  label,
  value,
  error,
  onValueChange,
}: {
  readonly label: string;
  readonly value: number | undefined;
  readonly error: string | undefined;
  readonly onValueChange: (next: number | undefined) => void;
}) {
  return (
    <Field.Root invalid={Boolean(error)}>
      <Field.Label>{label}</Field.Label>
      <MoneyInput value={value} onValueChange={onValueChange} />
      {error ? <Field.Error match>{error}</Field.Error> : null}
    </Field.Root>
  );
}

function VerdictFigure({ label, verdict }: { readonly label: string; readonly verdict: Verdict }) {
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
      <View style={styles.badgeRow}>
        <Badge intent={verdict.intent} size="sm">
          {verdict.label}
        </Badge>
      </View>
    </View>
  );
}

function Figure({ label, value }: { readonly label: string; readonly value: string }) {
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
        {value}
      </Text>
    </View>
  );
}

/**
 * The two tests and the figures they turn on, pinned above the form and
 * refreshed after every save. `aria-live` so the recalculation is heard.
 */
function SummaryBanner({ calculation }: { readonly calculation: LoadState<PlanCalculation> }) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const danger = { color: theme.colors.danger, fontFamily: theme.typography.body };
  if (calculation.kind === 'loading') {
    return (
      <View style={[styles.banner, { borderColor: theme.colors.line }]}>
        <Text style={[styles.note, muted]}>Calculating the plan…</Text>
      </View>
    );
  }
  if (calculation.kind === 'error') {
    return (
      <View style={[styles.banner, { borderColor: theme.colors.line }]}>
        <Text aria-live="assertive" style={[styles.note, danger]}>
          Could not calculate the plan.
        </Text>
      </View>
    );
  }
  const calc = calculation.value;
  return (
    <View aria-live="polite" style={[styles.banner, { borderColor: theme.colors.line }]}>
      <Figure
        label="Term"
        value={calc.funding.termMonths === null ? '—' : `${calc.funding.termMonths} months`}
      />
      <Figure label="Monthly payment" value={moneyText(calc.funding.basePayment)} />
      <Figure label="Paid in over the term" value={moneyText(calc.funding.total)} />
      <VerdictFigure label="Feasibility" verdict={feasibilityVerdict(calc)} />
      <Figure label="Unsecured creditors receive" value={percentText(calc.unsecured.percentage)} />
      <Figure label="Liquidation floor" value={percentText(calc.liquidation.percentage)} />
      <VerdictFigure label="Best-interests test" verdict={bestInterestsVerdict(calc)} />
      {calc.problems.map((problem) => (
        <Text key={problem} style={[styles.note, danger]}>
          {problem}
        </Text>
      ))}
      {calc.feasibility.reasons.map((reason) => (
        <Text key={reason} style={[styles.note, danger]}>
          {reason}
        </Text>
      ))}
      {calc.warnings.map((warning) => (
        <Text key={warning} style={[styles.note, muted]}>
          {warning}
        </Text>
      ))}
    </View>
  );
}

/** Each class, then its payees, every row with the source of its figure. */
function Waterfall({ calculation }: { readonly calculation: PlanCalculation }) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  return (
    <View style={styles.trace}>
      {calculation.classes.map((summary) => (
        <View key={summary.key} style={styles.trace}>
          <Heading level={3} size="body">
            {`${summary.label} — ${moneyText(summary.payout)} paid, ${monthsText(summary.firstMonth, summary.lastMonth).toLowerCase()}`}
          </Heading>
          <Table.Root dense>
            <Table.Head>
              <Table.Row>
                <Table.HeaderCell>Payee</Table.HeaderCell>
                <Table.HeaderCell width={110}>Owed</Table.HeaderCell>
                <Table.HeaderCell width={110}>Principal</Table.HeaderCell>
                <Table.HeaderCell width={100}>Interest</Table.HeaderCell>
                <Table.HeaderCell width={120}>Months</Table.HeaderCell>
                <Table.HeaderCell width={110}>Unpaid</Table.HeaderCell>
              </Table.Row>
            </Table.Head>
            <Table.Body>
              {summary.rows.map((row) => (
                <Table.Row key={row.key}>
                  <Table.Cell>{row.label}</Table.Cell>
                  <Table.Cell width={110}>{moneyText(row.allowed)}</Table.Cell>
                  <Table.Cell width={110}>{moneyText(row.principal)}</Table.Cell>
                  <Table.Cell width={100}>{moneyText(row.interest)}</Table.Cell>
                  <Table.Cell width={120}>{monthsText(row.firstMonth, row.lastMonth)}</Table.Cell>
                  <Table.Cell width={110}>{moneyText(row.unpaid)}</Table.Cell>
                </Table.Row>
              ))}
            </Table.Body>
          </Table.Root>
          {summary.rows.map((row) => (
            <Text key={row.key} style={[styles.note, muted]}>
              {`${row.label}: ${row.source}.`}
            </Text>
          ))}
        </View>
      ))}
      <Text style={[styles.note, muted]}>
        {`The unsecured class: ${moneyText(calculation.unsecured.poolTotal)} of claims — ${
          calculation.unsecured.targetSource ?? 'no target yet'
        }.`}
      </Text>
      {calculation.unsecured.pool.map((claim) => (
        <Text key={`${claim.claimId}-${claim.source}`} style={[styles.note, muted]}>
          {`${claim.label}: ${moneyText(claim.amount)} — ${claim.source}.`}
        </Text>
      ))}
    </View>
  );
}

/** Schedules A/B, C, D and E/F as a hypothetical Chapter 7 would read them. */
function LiquidationTable({ calculation }: { readonly calculation: PlanCalculation }) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };
  const liq = calculation.liquidation;
  return (
    <View style={styles.trace}>
      <Table.Root dense>
        <Table.Head>
          <Table.Row>
            <Table.HeaderCell>Asset</Table.HeaderCell>
            <Table.HeaderCell width={110}>Value</Table.HeaderCell>
            <Table.HeaderCell width={110}>Liens</Table.HeaderCell>
            <Table.HeaderCell width={110}>Exempt</Table.HeaderCell>
            <Table.HeaderCell width={110}>Unexempt</Table.HeaderCell>
          </Table.Row>
        </Table.Head>
        <Table.Body>
          {liq.assets.map((asset) => (
            <Table.Row key={asset.assetId}>
              <Table.Cell>{asset.description ?? 'Unnamed asset'}</Table.Cell>
              <Table.Cell width={110}>{moneyText(asset.value)}</Table.Cell>
              <Table.Cell width={110}>{moneyText(asset.liens)}</Table.Cell>
              <Table.Cell width={110}>{moneyText(asset.exempt)}</Table.Cell>
              <Table.Cell width={110}>{moneyText(asset.unexempt)}</Table.Cell>
            </Table.Row>
          ))}
        </Table.Body>
      </Table.Root>
      <Text style={[styles.row, ink]}>
        {`Unexempt ${moneyText(liq.unexemptTotal)} − trustee’s commission ${moneyText(liq.trusteeCommission)} − other costs ${moneyText(liq.otherCosts)} − priority claims ${moneyText(liq.priorityTotal)} = ${moneyText(liq.available)} for ${moneyText(liq.poolTotal)} of unsecured claims: ${percentText(liq.percentage)}.`}
      </Text>
      <Text style={[styles.note, muted]}>
        {`${liq.trusteeCommissionRule}. Other costs: ${liq.otherCostsSource}. ${calculation.bestInterests.rule}.`}
      </Text>
    </View>
  );
}

/**
 * An unsaved alternative: the same plan with a different monthly payment
 * and term, calculated side by side with the current one. Nothing is
 * written; "Use this alternative" copies it into the form for Save.
 */
function ScenarioPanel({
  caseId,
  body,
  current,
  onAdopt,
}: {
  readonly caseId: string;
  readonly body: PlanBody;
  readonly current: PlanCalculation | null;
  readonly onAdopt: (next: PlanBody) => void;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const [payment, setPayment] = useState('');
  const [term, setTerm] = useState('');
  const [result, setResult] = useState<PlanScenario | null>(null);
  const [message, setMessage] = useState('');

  const alternative: PlanBody = {
    ...body,
    paymentSource: 'fixed',
    monthlyPayment: moneyOf(payment),
    ...(wholeNumberOf(term) === undefined ? {} : { termMonths: wholeNumberOf(term) }),
  };

  const compare = async () => {
    setMessage('Calculating…');
    try {
      const response = await call((client) =>
        client.calculatePlanScenarios(caseId, [{ label: 'Alternative', plan: alternative }]),
      );
      if (!response.ok) {
        setMessage('');
        return;
      }
      setResult(response.value[0] ?? null);
      setMessage('');
    } catch (cause) {
      setMessage(
        cause instanceof ApiValidationException
          ? Object.values(cause.fields).join(' ')
          : 'Could not calculate the alternative.',
      );
    }
  };

  return (
    <View style={styles.trace}>
      <Text style={[styles.note, muted]}>
        Try a different monthly payment or term against the same case. Nothing is saved until you
        use the alternative and then save the plan.
      </Text>
      <TextField
        label="Alternative monthly payment"
        value={payment}
        error={undefined}
        onValueChange={setPayment}
      />
      <TextField
        label="Alternative term in months (blank: as entered)"
        value={term}
        error={undefined}
        onValueChange={setTerm}
      />
      <View style={styles.actions}>
        <Button
          size="lg"
          intent="secondary"
          disabled={moneyOf(payment) === undefined}
          onPress={() => void compare()}
        >
          Compare
        </Button>
        <Text aria-live="polite" style={[styles.status, muted]}>
          {message}
        </Text>
      </View>
      {result === null ? null : (
        <>
          <Table.Root dense>
            <Table.Head>
              <Table.Row>
                <Table.HeaderCell>Measure</Table.HeaderCell>
                <Table.HeaderCell width={170}>As entered</Table.HeaderCell>
                <Table.HeaderCell width={170}>Alternative</Table.HeaderCell>
              </Table.Row>
            </Table.Head>
            <Table.Body>
              {[
                {
                  measure: 'Paid in over the term',
                  now: current === null ? '—' : moneyText(current.funding.total),
                  alt: moneyText(result.calculation.funding.total),
                },
                {
                  measure: 'Feasibility',
                  now: current === null ? '—' : feasibilityVerdict(current).label,
                  alt: feasibilityVerdict(result.calculation).label,
                },
                {
                  measure: 'Unsecured creditors receive',
                  now: current === null ? '—' : percentText(current.unsecured.percentage),
                  alt: percentText(result.calculation.unsecured.percentage),
                },
                {
                  measure: 'Best-interests test',
                  now: current === null ? '—' : bestInterestsVerdict(current).label,
                  alt: bestInterestsVerdict(result.calculation).label,
                },
              ].map((row) => (
                <Table.Row key={row.measure}>
                  <Table.Cell>{row.measure}</Table.Cell>
                  <Table.Cell width={170}>{row.now}</Table.Cell>
                  <Table.Cell width={170}>{row.alt}</Table.Cell>
                </Table.Row>
              ))}
            </Table.Body>
          </Table.Root>
          <View style={styles.actions}>
            <Button size="lg" intent="secondary" onPress={() => onAdopt(alternative)}>
              Use this alternative
            </Button>
          </View>
        </>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  actions: { alignItems: 'flex-start', gap: spacing.sm },
  badgeRow: { flexDirection: 'row', marginTop: spacing.xs },
  banner: {
    alignItems: 'flex-end',
    borderRadius: 8,
    borderWidth: 1,
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.lg,
    padding: spacing.md,
  },
  figure: { minWidth: 160 },
  figureLabel: { fontSize: fontSizes.label },
  figureValue: { fontSize: fontSizes.section, marginTop: spacing.xs },
  intro: { fontSize: fontSizes.label, lineHeight: fontSizes.label * 1.5 },
  note: { fontSize: fontSizes.label, lineHeight: fontSizes.label * 1.5 },
  row: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  rowGroup: { gap: spacing.sm },
  section: { gap: spacing.sm, marginTop: spacing.md },
  status: { fontSize: fontSizes.label },
  trace: { gap: spacing.sm },
});
