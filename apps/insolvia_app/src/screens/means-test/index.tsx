import {
  ApiValidationException,
  EXCLUDED_INCOME_CATEGORIES,
  MARITAL_FILING_STATUSES,
  OTHER_INCOME_CATEGORIES,
  UNEMPLOYMENT_AS_SSA,
  staffTypedProvenance,
} from '@insolvia-ai/api-client';
import type {
  CaseEntityRequest,
  CaseMeansTest,
  CmiColumn,
  IncomeColumn,
  IncomeLineCategory,
  MaritalFilingStatus,
  MeansTestInputBody,
  PetitionBody,
  PresumptionExemption,
} from '@insolvia-ai/api-client';
import {
  Badge,
  Button,
  Checkbox,
  Field,
  Input,
  Select,
  Table,
  Textarea,
} from '@insolvia-ai/design-system';
import { Link } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { CaseColumn, useCase } from '@/components/case-shell';
import { Heading } from '@/components/heading';
import { CollectionEditor } from '@/screens/intake/collection-editor';
import { COLLECTION_SPECS, labelize } from '@/screens/intake/collections';
import { newRowId } from '@/screens/intake/row-id';
import { formatFormDate } from '@/screens/petition/statutory-dates';
import { fontSizes, spacing, useTheme } from '@/theme';

import { inputBodyOf, overrideFor, verdictOf, withOverride } from './inputs';

/**
 * `/cases/<id>/means-test` — the § 707(b) means test (issue #349).
 *
 * THE VERDICT IS THE SERVER'S. `GET /v1/cases/{id}/means-test` runs the same
 * engine the packet's B122A-1 and B122A-2 use and returns the whole trace;
 * this screen renders it and computes nothing (ADR 0001). Every save of the
 * inputs below re-reads it, which is what "recomputes on every save" means
 * here — the recomputation happens where the rules live.
 *
 * Three kinds of thing are on the page, and they are kept visibly apart:
 *
 * 1. **Inputs only the debtor can supply**, all on the ONE `means_test_input`
 *    record a case carries: the marital and filing status, the three
 *    household sizes (each independently overridable, because the Census
 *    median, the IRS family size and the IRS housing standard legitimately
 *    count different people), the Form 122A-1Supp exemptions that end the
 *    test, the per-line income overrides, and the B122A-2 deductions. One
 *    explicit Save, like the petition — a half-typed monthly figure is not
 *    something to persist on a debounce.
 * 2. **Facts that live elsewhere and are shown here with a link**: the
 *    expected filing date (the petition's — one date, one owner), the
 *    dependents (their own collection, edited in place with the generic
 *    editor), and the per-claim secured payments (entered beside the claim,
 *    in `ClaimSecuredPaymentPanel`, and read back here from the trace).
 * 3. **The trace**: the CMI derivation per column with the window it used,
 *    the median comparison, and for an above-median debtor the B122A-2 lines
 *    with their sources.
 */

type LoadState<T> =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly value: T }
  | { readonly kind: 'error' };

const DEPENDENT_SPEC = COLLECTION_SPECS.find((spec) => spec.collection === 'dependents');

const MARITAL_OPTIONS = MARITAL_FILING_STATUSES.map((value) => ({
  value,
  label: {
    not_married: 'Not married',
    married_filing_jointly: 'Married and filing jointly',
    married_not_filing_same_household: 'Married, spouse not filing — same household',
    married_not_filing_separated: 'Married, spouse not filing — living separately',
  }[value],
}));

const YES_NO_OPTIONS = [
  { value: 'yes', label: 'Yes' },
  { value: 'no', label: 'No' },
] as const;

/** B122A-1 lines 2-10, in the form's order, with the exclusions after them. */
const COUNTED_LINES: readonly { readonly category: IncomeLineCategory; readonly label: string }[] =
  [
    {
      category: 'wages',
      label: 'Line 2 — Gross wages, salary, tips, bonuses, overtime, commissions',
    },
    { category: 'alimony_maintenance', label: 'Line 3 — Alimony and maintenance payments' },
    {
      category: 'household_contributions',
      label: 'Line 4 — Amounts contributed by others to household expenses',
    },
    { category: 'business', label: 'Line 5 — Net income from a business, profession, or farm' },
    { category: 'rental', label: 'Line 6 — Net income from rental and other real property' },
    {
      category: 'interest_dividends_royalties',
      label: 'Line 7 — Interest, dividends, and royalties',
    },
    { category: 'unemployment', label: 'Line 8 — Unemployment compensation' },
    { category: 'pension_retirement', label: 'Line 9 — Pension or retirement income' },
    { category: 'other', label: 'Line 10 — Income from all other sources' },
  ];

const EXCLUSION_LINES: readonly {
  readonly category: IncomeLineCategory;
  readonly label: string;
}[] = [
  { category: 'social_security_act_benefit', label: 'Social Security Act benefits' },
  { category: 'veterans_disability_compensation', label: 'Veterans’ disability compensation' },
  { category: 'war_crime_victim_payment', label: 'Payments to victims of war crimes' },
  { category: 'terrorism_victim_payment', label: 'Payments to victims of terrorism' },
  { category: UNEMPLOYMENT_AS_SSA, label: 'Unemployment claimed as a Social Security Act benefit' },
];

// Every other income category the API knows must be on the grid, or an
// override could be stored that no row shows. Pinned at module load, where a
// mismatch fails the first test that imports the screen.
const GRID_CATEGORIES = new Set([...COUNTED_LINES, ...EXCLUSION_LINES].map((row) => row.category));
for (const category of [...OTHER_INCOME_CATEGORIES, ...EXCLUDED_INCOME_CATEGORIES]) {
  if (!GRID_CATEGORIES.has(category)) {
    throw new Error(`the means-test income grid has no row for ${category}`);
  }
}

const EXEMPTIONS: readonly {
  readonly key: PresumptionExemption;
  readonly label: string;
}[] = [
  {
    key: 'non_consumer_debts',
    label: 'The debts are not primarily consumer debts (§ 707(b)(1))',
  },
  {
    key: 'disabled_veteran',
    label:
      'Disabled veteran whose debts were incurred primarily during active duty or ' +
      'homeland-defense activity (§ 707(b)(2)(D)(i))',
  },
  {
    key: 'reservist_national_guard',
    label:
      'Reservist or National Guard member called to active duty after September 11, 2001 ' +
      '(§ 707(b)(2)(D)(ii))',
  },
];

type MoneyKey = {
  [K in keyof MeansTestInputBody]-?: MeansTestInputBody[K] extends string | undefined ? K : never;
}[keyof MeansTestInputBody];

/** The B122A-2 deductions the debtor supplies, in the form's line order. */
const DEDUCTION_FIELDS: readonly { readonly key: MoneyKey; readonly label: string }[] = [
  {
    key: 'home_secured_monthly_total',
    label:
      'Line 9b — average monthly payment on debts secured by the home (leave blank to use ' +
      'the per-claim rows)',
  },
  {
    key: 'housing_adjustment_amount',
    label: 'Line 10 — claimed adjustment to the housing standard',
  },
  {
    key: 'vehicle_1_loan_monthly',
    label:
      'Line 13b — average monthly payment on Vehicle 1 (leave blank to use the per-claim rows)',
  },
  {
    key: 'vehicle_2_loan_monthly',
    label:
      'Line 13e — average monthly payment on Vehicle 2 (leave blank to use the per-claim rows)',
  },
  { key: 'additional_public_transportation', label: 'Line 15 — additional public transportation' },
  { key: 'taxes', label: 'Line 16 — taxes' },
  { key: 'involuntary_deductions', label: 'Line 17 — involuntary deductions' },
  { key: 'term_life_insurance', label: 'Line 18 — term life insurance' },
  { key: 'court_ordered_payments', label: 'Line 19 — court-ordered payments' },
  {
    key: 'education_for_employment_or_disability',
    label: 'Line 20 — education for employment or for a disabled child',
  },
  { key: 'childcare', label: 'Line 21 — childcare' },
  { key: 'healthcare_above_allowance', label: 'Line 22 — additional health care expenses' },
  { key: 'optional_telecom', label: 'Line 23 — optional telephones and telephone services' },
  { key: 'health_insurance', label: 'Line 25 — health insurance' },
  { key: 'disability_insurance', label: 'Line 25 — disability insurance' },
  { key: 'health_savings_account', label: 'Line 25 — health savings account' },
  { key: 'family_care_contributions', label: 'Line 26 — care of household or family members' },
  { key: 'family_violence_protection', label: 'Line 27 — protection against family violence' },
  { key: 'home_energy_excess', label: 'Line 28 — additional home energy costs' },
  { key: 'education_under_18', label: 'Line 29 — education for dependent children under 18' },
  { key: 'additional_food_clothing', label: 'Line 30 — additional food and clothing' },
  { key: 'charitable_contributions', label: 'Line 31 — continuing charitable contributions' },
  {
    key: 'priority_cure_total',
    label:
      'Line 34 — total past due on secured debts necessary for support (leave blank to use the per-claim rows)',
  },
  { key: 'ch13_projected_plan_payment', label: 'Line 36 — projected Chapter 13 plan payment' },
];

export interface MeansTestProps {
  readonly caseId: string;
}

export function MeansTest({ caseId }: MeansTestProps) {
  const theme = useTheme();
  const { call } = useApi();
  const { debtors } = useCase();

  const [trace, setTrace] = useState<LoadState<CaseMeansTest>>({ kind: 'loading' });
  const [petition, setPetition] = useState<PetitionBody | null>(null);
  const [inputId, setInputId] = useState<string | null>(null);
  const [body, setBody] = useState<MeansTestInputBody>({});
  const [inputsLoaded, setInputsLoaded] = useState(false);
  const [status, setStatus] = useState('');
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [saving, setSaving] = useState(false);

  const loadTrace = useCallback(async () => {
    try {
      const result = await call((client) => client.getCaseMeansTest(caseId));
      if (result.ok) setTrace({ kind: 'ready', value: result.value });
    } catch {
      setTrace({ kind: 'error' });
    }
  }, [call, caseId]);

  useEffect(() => {
    void loadTrace();
  }, [loadTrace]);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const inputs = await call((client) => client.listCaseEntities(caseId, 'means_test_inputs'));
        if (!inputs.ok || cancelled) return;
        // ONE per case by meaning (docs/reference/case-data-model.md); the
        // first record is the one this screen edits, as the petition does.
        const first = inputs.value[0];
        if (first !== undefined) {
          setInputId(first.id);
          setBody(inputBodyOf(first as unknown as Record<string, unknown>));
        }
        setInputsLoaded(true);
      } catch {
        if (!cancelled) setStatus('Could not load the entered figures.');
      }
      try {
        const petitions = await call((client) => client.listCaseEntities(caseId, 'petitions'));
        if (!petitions.ok || cancelled) return;
        const first = petitions.value[0];
        if (first !== undefined) setPetition(first as PetitionBody);
      } catch {
        // The date is shown for orientation; the petition screen owns it.
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
      // The whole record, with staff_typed provenance derived from what is
      // filled in — the same rule every other editor applies. Nothing on
      // this record is ever extracted, so rebuilding the map is exact.
      const request = {
        ...body,
        provenance: staffTypedProvenance(body as unknown as Record<string, unknown>),
      } as unknown as CaseEntityRequest<'means_test_inputs'>;
      const result = await call((client) =>
        inputId === null
          ? client.addCaseEntity(caseId, 'means_test_inputs', request)
          : client.putCaseEntity(caseId, 'means_test_inputs', inputId, request),
      );
      if (!result.ok) {
        setStatus('');
        return;
      }
      setInputId(result.value.id);
      setBody(inputBodyOf(result.value as unknown as Record<string, unknown>));
      setErrors({});
      setStatus('Saved — recomputing');
      await loadTrace();
      setStatus('Saved');
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setErrors(cause.fields);
        setStatus('Some answers need attention.');
      } else {
        setStatus('Could not save. Your entries are still here — try again.');
      }
    } finally {
      setSaving(false);
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  const setMoney = (key: MoneyKey, next: string) =>
    setBody((current) => ({ ...current, [key]: next === '' ? undefined : next }));
  const setCount = (
    key:
      | 'people_under_65'
      | 'people_65_or_older'
      | 'median_household_size'
      | 'irs_family_size'
      | 'irs_housing_family_size'
      | 'vehicle_count',
    next: string,
  ) => {
    const digits = next.replaceAll(/[^0-9]/gu, '');
    setBody((current) => ({ ...current, [key]: digits === '' ? undefined : Number(digits) }));
  };

  const ready = trace.kind === 'ready' ? trace.value : null;
  const hasColumnB =
    debtors.some((debtor) => debtor.filing_role !== 'debtor_1') ||
    (ready?.cmi.columns.some((column) => column.column === 'B') ?? false) ||
    (body.income_overrides?.some((row) => row.column === 'B') ?? false);
  const columns: readonly IncomeColumn[] = hasColumnB ? ['A', 'B'] : ['A'];

  // The button appears twice (under the verdict, and after the last field)
  // because the page is long; the status is announced ONCE, beside the top
  // one, so a screen reader does not hear every save twice.
  const saveButton = (
    <Button size="lg" disabled={saving || !inputsLoaded} onPress={() => void persist()}>
      Save and recompute
    </Button>
  );
  const saveBar = (
    <View style={styles.actions}>
      {saveButton}
      <Text aria-live="polite" style={[styles.status, muted]}>
        {status}
      </Text>
    </View>
  );

  return (
    <CaseColumn>
      <Heading level={1}>Means test</Heading>
      <Text style={[styles.intro, muted]}>
        The § 707(b) determination as of the expected filing date, computed by the same engine that
        prints Forms 122A-1 and 122A-2. Every figure below names the rule, input or dataset it came
        from; nothing on this page is added up here.
      </Text>

      <VerdictBanner trace={trace} />
      {saveBar}

      <Section title="Expected filing date">
        <Text style={[styles.row, ink]}>
          {petition?.expected_filing_date === undefined
            ? 'No expected filing date is set — the case’s opening date stands in for it.'
            : `Planned for ${formatFormDate(petition.expected_filing_date)}.`}
          {ready === null
            ? ''
            : ` The six-month window runs ${ready.cmi.window.start} to ${ready.cmi.window.end}.`}
        </Text>
        <Link
          href={`/cases/${caseId}/petition`}
          style={[styles.link, { color: theme.colors.primary, fontFamily: theme.typography.body }]}
        >
          Change the expected filing date on the petition
        </Link>
      </Section>

      <Section title="Marital and filing status (122A-1 line 1)">
        <Field.Root invalid={Boolean(errors.marital_filing_status)}>
          <Field.Label>Marital and filing status</Field.Label>
          <Select
            options={MARITAL_OPTIONS}
            value={body.marital_filing_status ?? null}
            onValueChange={(next) =>
              setBody((current) => ({
                ...current,
                marital_filing_status: MARITAL_FILING_STATUSES.includes(next as MaritalFilingStatus)
                  ? (next as MaritalFilingStatus)
                  : undefined,
              }))
            }
            placeholder="From the debtor records"
          />
          <Field.Description>
            {ready === null
              ? 'Leave unset to answer from the debtor records.'
              : `Currently: ${ready.maritalFilingStatus.value === null ? 'undetermined' : labelize(ready.maritalFilingStatus.value)} — ${ready.maritalFilingStatus.source}.`}
          </Field.Description>
          {errors.marital_filing_status ? (
            <Field.Error match>{errors.marital_filing_status}</Field.Error>
          ) : null}
        </Field.Root>
      </Section>

      <Section title="Household">
        <CountField
          label="People under 65"
          value={body.people_under_65}
          error={errors.people_under_65}
          onValueChange={(next) => setCount('people_under_65', next)}
        />
        <CountField
          label="People 65 or older"
          value={body.people_65_or_older}
          error={errors.people_65_or_older}
          onValueChange={(next) => setCount('people_65_or_older', next)}
        />
        <CountField
          label="Household size for the median table (122A-1 line 13) — override"
          value={body.median_household_size}
          error={errors.median_household_size}
          description={figureNote(ready?.household.medianHouseholdSize)}
          onValueChange={(next) => setCount('median_household_size', next)}
        />
        <CountField
          label="IRS family size (122A-2 line 5) — override"
          value={body.irs_family_size}
          error={errors.irs_family_size}
          description={figureNote(ready?.household.irsFamilySize)}
          onValueChange={(next) => setCount('irs_family_size', next)}
        />
        <CountField
          label="IRS housing family size (122A-2 lines 8–9a) — override"
          value={body.irs_housing_family_size}
          error={errors.irs_housing_family_size}
          description={figureNote(ready?.household.irsHousingFamilySize)}
          onValueChange={(next) => setCount('irs_housing_family_size', next)}
        />
        <CountField
          label="Vehicles claimed (122A-2 line 11) — at most 2"
          value={body.vehicle_count}
          error={errors.vehicle_count}
          onValueChange={(next) => setCount('vehicle_count', next)}
        />
      </Section>

      <Section title="Exemptions from the presumption (Form 122A-1Supp)">
        <Text style={[styles.note, muted]}>
          Any one of these ends the test before the median comparison.
          {ready?.exemptions.applied === null || ready === null
            ? ''
            : ` Applied: ${ready.exemptions.rule ?? ready.exemptions.applied}.`}
        </Text>
        {EXEMPTIONS.map((exemption) => (
          <View key={exemption.key} style={styles.checkboxRow}>
            <Checkbox.Root
              aria-label={exemption.label}
              checked={body[exemption.key] === true}
              onCheckedChange={(checked) =>
                setBody((current) => ({ ...current, [exemption.key]: checked ? true : undefined }))
              }
            >
              <Checkbox.Indicator>✓</Checkbox.Indicator>
            </Checkbox.Root>
            <Text aria-hidden style={[styles.checkboxLabel, ink]}>
              {exemption.label}
            </Text>
          </View>
        ))}
      </Section>

      <Section title="Dependents">
        <Text style={[styles.note, muted]}>
          Dependents under 18 cap line 29’s education expense
          {ready === null ? '' : ` — ${ready.household.childrenUnder18} counted`}. Edited here, on
          their own records.
        </Text>
        {DEPENDENT_SPEC !== undefined ? (
          <CollectionEditor caseId={caseId} spec={DEPENDENT_SPEC} />
        ) : null}
      </Section>

      <Section title="Income (122A-1 lines 2–10)">
        <Text style={[styles.note, muted]}>
          Each line is derived from the pay records and other income received in the window. Enter a
          monthly figure to replace a line; leave it blank to use the records. The exclusions are
          shown and never counted.
        </Text>
        {columns.map((column) => (
          <IncomeGrid
            key={column}
            column={column}
            trace={ready}
            body={body}
            errors={errors}
            onOverride={(category, next) =>
              setBody((current) => withOverride(current, column, category, next, newRowId))
            }
          />
        ))}
      </Section>

      <Section title="Marital adjustment (122A-2 line 3)">
        <Text style={[styles.note, muted]}>
          Parts of a non-filing spouse’s income not paid for the household, listed separately.
        </Text>
        {(body.marital_adjustments ?? []).map((item) => (
          <View key={item.id} style={styles.adjustment}>
            <Field.Root invalid={Boolean(errors[`marital_adjustments[${item.id}].description`])}>
              <Field.Label>Purpose</Field.Label>
              <Input
                value={item.description ?? ''}
                onValueChange={(next) =>
                  setBody((current) => ({
                    ...current,
                    marital_adjustments: (current.marital_adjustments ?? []).map((row) =>
                      row.id === item.id
                        ? { ...row, description: next === '' ? undefined : next }
                        : row,
                    ),
                  }))
                }
              />
            </Field.Root>
            <Field.Root invalid={Boolean(errors[`marital_adjustments[${item.id}].amount`])}>
              <Field.Label>Monthly amount</Field.Label>
              <Input
                value={item.amount ?? ''}
                onValueChange={(next) =>
                  setBody((current) => ({
                    ...current,
                    marital_adjustments: (current.marital_adjustments ?? []).map((row) =>
                      row.id === item.id ? { ...row, amount: next === '' ? undefined : next } : row,
                    ),
                  }))
                }
              />
            </Field.Root>
            <Button
              size="lg"
              intent="secondary"
              onPress={() =>
                setBody((current) => {
                  const rest = (current.marital_adjustments ?? []).filter(
                    (row) => row.id !== item.id,
                  );
                  const { marital_adjustments: _removed, ...without } = current;
                  return rest.length === 0 ? without : { ...current, marital_adjustments: rest };
                })
              }
            >
              Remove
            </Button>
          </View>
        ))}
        <Button
          size="lg"
          intent="secondary"
          onPress={() =>
            setBody((current) => ({
              ...current,
              marital_adjustments: [...(current.marital_adjustments ?? []), { id: newRowId() }],
            }))
          }
        >
          Add a marital adjustment
        </Button>
      </Section>

      <Section title="Deductions (122A-2)">
        <Text style={[styles.note, muted]}>
          Monthly figures only the debtor can supply. The IRS allowances and the per-claim secured
          payments are read from the records — enter a per-claim payment beside the claim on the
          intake screen.
        </Text>
        {DEDUCTION_FIELDS.map((field) => (
          <Field.Root key={field.key} invalid={Boolean(errors[field.key])}>
            <Field.Label>{field.label}</Field.Label>
            <Input
              value={body[field.key] ?? ''}
              onValueChange={(next) => setMoney(field.key, next)}
              autoCorrect={false}
            />
            {errors[field.key] ? <Field.Error match>{errors[field.key]}</Field.Error> : null}
          </Field.Root>
        ))}
        <Field.Root invalid={Boolean(errors.housing_adjustment_explanation)}>
          <Field.Label>Line 10 — why the housing standard’s split is incorrect</Field.Label>
          <Textarea
            value={body.housing_adjustment_explanation ?? ''}
            onValueChange={(next) =>
              setBody((current) => ({
                ...current,
                housing_adjustment_explanation: next === '' ? undefined : next,
              }))
            }
          />
          {errors.housing_adjustment_explanation ? (
            <Field.Error match>{errors.housing_adjustment_explanation}</Field.Error>
          ) : null}
        </Field.Root>
        <Field.Root invalid={Boolean(errors.ch13_eligible)}>
          <Field.Label>Line 36 — eligible to file under Chapter 13?</Field.Label>
          <Select
            options={[...YES_NO_OPTIONS]}
            value={body.ch13_eligible === true ? 'yes' : body.ch13_eligible === false ? 'no' : null}
            onValueChange={(next) =>
              setBody((current) => ({
                ...current,
                ch13_eligible: next === 'yes' ? true : next === 'no' ? false : undefined,
              }))
            }
            placeholder="Not answered"
          />
          {errors.ch13_eligible ? <Field.Error match>{errors.ch13_eligible}</Field.Error> : null}
        </Field.Root>
      </Section>

      <View style={styles.actions}>{saveButton}</View>

      <Section title="The trace">
        <Trace trace={trace} />
      </Section>
    </CaseColumn>
  );
}

function figureNote(
  figure: { readonly value: number | null; readonly source: string | null } | undefined,
): string {
  if (figure === undefined) return 'Leave blank to use the two counts above.';
  if (figure.value === null)
    return 'Not yet determined — enter the two counts above, or override here.';
  return `Currently ${figure.value} — ${figure.source ?? ''}.`;
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

function CountField({
  label,
  value,
  error,
  description,
  onValueChange,
}: {
  readonly label: string;
  readonly value: number | undefined;
  readonly error: string | undefined;
  readonly description?: string | undefined;
  readonly onValueChange: (next: string) => void;
}) {
  return (
    <Field.Root invalid={Boolean(error)}>
      <Field.Label>{label}</Field.Label>
      <Input
        value={value === undefined ? '' : String(value)}
        onValueChange={onValueChange}
        autoCorrect={false}
      />
      {description !== undefined ? <Field.Description>{description}</Field.Description> : null}
      {error ? <Field.Error match>{error}</Field.Error> : null}
    </Field.Root>
  );
}

/**
 * The pinned verdict: CMI, median, under/over, presumption — the server's
 * four answers, refreshed after every save. `aria-live` so a screen reader
 * hears the recomputation land.
 */
function VerdictBanner({ trace }: { readonly trace: LoadState<CaseMeansTest> }) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  if (trace.kind === 'loading') {
    return (
      <View style={[styles.banner, { borderColor: theme.colors.line }]}>
        <Text style={[styles.note, muted]}>Computing the means test…</Text>
      </View>
    );
  }
  if (trace.kind === 'error') {
    return (
      <View style={[styles.banner, { borderColor: theme.colors.line }]}>
        <Text
          aria-live="assertive"
          style={[styles.note, { color: theme.colors.danger, fontFamily: theme.typography.body }]}
        >
          Could not compute the means test.
        </Text>
      </View>
    );
  }
  const verdict = verdictOf(trace.value);
  return (
    <View aria-live="polite" style={[styles.banner, { borderColor: theme.colors.line }]}>
      <MoneyFigure label="Current monthly income" value={verdict.cmi} />
      <MoneyFigure label="Applicable annual median" value={verdict.median} />
      <MoneyFigure label="Median comparison" value={verdict.position} text />
      <View style={styles.figure}>
        <Text style={[styles.figureLabel, muted]}>Presumption of abuse</Text>
        <View style={styles.badgeRow}>
          <Badge intent={verdict.intent} size="sm">
            {verdict.presumption}
          </Badge>
        </View>
      </View>
      {trace.value.problems.length > 0 ? (
        <Text style={[styles.note, muted]}>{trace.value.problems.join(' ')}</Text>
      ) : null}
    </View>
  );
}

function MoneyFigure({
  label,
  value,
  text = false,
}: {
  readonly label: string;
  readonly value: string;
  readonly text?: boolean;
}) {
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
        style={[
          styles.figureValue,
          {
            color: theme.colors.ink,
            fontFamily: text ? theme.typography.body : theme.typography.mono,
          },
        ]}
      >
        {value}
      </Text>
    </View>
  );
}

/**
 * One column of B122A-1 lines 2-10: what the records derived, and the
 * override box beside it. The derived figure comes from the trace and is
 * never recomputed here; the override is the input the next save sends.
 */
function IncomeGrid({
  column,
  trace,
  body,
  errors,
  onOverride,
}: {
  readonly column: IncomeColumn;
  readonly trace: CaseMeansTest | null;
  readonly body: MeansTestInputBody;
  readonly errors: Readonly<Record<string, string>>;
  readonly onOverride: (category: IncomeLineCategory, next: string) => void;
}) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const derived: CmiColumn | undefined = trace?.cmi.columns.find((c) => c.column === column);
  const heading =
    column === 'A' ? 'Column A — Debtor 1' : 'Column B — Debtor 2 or non-filing spouse';

  const derivedFor = (category: IncomeLineCategory): string => {
    const line =
      derived?.lines.find((l) => l.category === category) ??
      derived?.excluded.find((l) => l.category === category);
    if (line === undefined) return '—';
    return line.note === '' ? `$${line.monthlyAverage}` : `$${line.monthlyAverage} (${line.note})`;
  };

  const rows = (
    lines: readonly { readonly category: IncomeLineCategory; readonly label: string }[],
  ) =>
    lines.map((line) => {
      const override = overrideFor(body, column, line.category);
      const errorKey = Object.keys(errors).find(
        (path) =>
          override !== undefined &&
          path.startsWith(`income_overrides[`) &&
          path.includes(override.id),
      );
      return (
        <View key={line.category} style={styles.gridRow}>
          <Field.Root invalid={errorKey !== undefined}>
            <Field.Label>{`${heading}: ${line.label}`}</Field.Label>
            <Input
              value={override?.monthly_amount ?? ''}
              onValueChange={(next) => onOverride(line.category, next)}
              autoCorrect={false}
            />
            <Field.Description>{`From the records: ${derivedFor(line.category)}`}</Field.Description>
            {errorKey !== undefined ? <Field.Error match>{errors[errorKey]}</Field.Error> : null}
          </Field.Root>
        </View>
      );
    });

  return (
    <View style={styles.grid}>
      <Heading level={3} size="body">
        {heading}
      </Heading>
      {rows(COUNTED_LINES)}
      <Text style={[styles.note, muted]}>
        Excluded by § 101(10A)(B)(ii) — shown on the form, never counted:
      </Text>
      {rows(EXCLUSION_LINES)}
      <Text style={[styles.note, muted]}>
        {derived === undefined
          ? 'No records in the window for this column yet.'
          : `Column total from the records and overrides: $${derived.monthlyTotal} per month.`}
      </Text>
    </View>
  );
}

/** The trace, read-only: the comparison, the B122A-2 lines, the gaps and problems. */
function Trace({ trace }: { readonly trace: LoadState<CaseMeansTest> }) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };
  if (trace.kind !== 'ready') {
    return <Text style={[styles.note, muted]}>Waiting for the calculation.</Text>;
  }
  const value = trace.value;
  return (
    <View style={styles.trace}>
      <Text style={[styles.row, ink]}>
        {`Window ${value.cmi.window.start} to ${value.cmi.window.end} (${value.asOfSource}). Combined CMI $${value.cmi.combinedMonthlyTotal} per month, $${value.cmi.annualized} annualized.`}
      </Text>
      {value.comparison === null ? null : (
        <Text style={[styles.row, ink]}>
          {`${value.comparison.state} household of ${value.comparison.householdSize}: median $${value.comparison.annualMedian} — ${value.comparison.source}.`}
        </Text>
      )}
      {value.cmi.gaps.map((gap) => (
        <Text key={gap.employer} style={[styles.note, muted]}>
          {`No paycheck from ${gap.employer} in ${gap.months.join(', ')}.`}
        </Text>
      ))}
      {value.problems.map((problem) => (
        <Text
          key={problem}
          style={[styles.note, { color: theme.colors.danger, fontFamily: theme.typography.body }]}
        >
          {problem}
        </Text>
      ))}
      {value.lines.length === 0 ? (
        <Text style={[styles.note, muted]}>
          {value.outcome === 'below_median'
            ? 'Below the median — Form 122A-2 is not filed.'
            : value.outcome === 'exempt'
              ? 'Exempt — Form 122A-2 is not filed.'
              : 'The 122A-2 lines appear once the test can run.'}
        </Text>
      ) : (
        <Table.Root dense>
          <Table.Head>
            <Table.Row>
              <Table.HeaderCell width={60}>Line</Table.HeaderCell>
              <Table.HeaderCell>Subject</Table.HeaderCell>
              <Table.HeaderCell width={120}>Amount</Table.HeaderCell>
              <Table.HeaderCell>Source</Table.HeaderCell>
            </Table.Row>
          </Table.Head>
          <Table.Body>
            {value.lines.map((line) => (
              <Table.Row key={line.line}>
                <Table.Cell width={60}>{line.line}</Table.Cell>
                <Table.Cell>{line.label}</Table.Cell>
                <Table.Cell width={120}>{`$${line.amount}`}</Table.Cell>
                <Table.Cell>{line.source}</Table.Cell>
              </Table.Row>
            ))}
          </Table.Body>
        </Table.Root>
      )}
      <Text style={[styles.note, muted]}>
        {`Datasets: ${Object.values(value.releaseIds).join('; ') || 'none resolved'}.`}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  actions: { alignItems: 'flex-start', gap: spacing.sm },
  adjustment: { gap: spacing.sm },
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
  checkboxLabel: { flexShrink: 1, fontSize: fontSizes.body },
  checkboxRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
  figure: { minWidth: 160 },
  figureLabel: { fontSize: fontSizes.label },
  figureValue: { fontSize: fontSizes.section, marginTop: spacing.xs },
  grid: { gap: spacing.sm },
  gridRow: {},
  intro: { fontSize: fontSizes.label, lineHeight: fontSizes.label * 1.5 },
  link: { fontSize: fontSizes.label, minHeight: 44, paddingVertical: spacing.sm },
  note: { fontSize: fontSizes.label, lineHeight: fontSizes.label * 1.5 },
  row: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  section: { gap: spacing.sm, marginTop: spacing.md },
  status: { fontSize: fontSizes.label },
  trace: { gap: spacing.sm },
});
