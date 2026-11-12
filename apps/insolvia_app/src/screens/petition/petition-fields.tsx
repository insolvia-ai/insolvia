import {
  DEBT_CHARACTERS,
  ESTIMATED_CREDITORS_BANDS,
  ESTIMATED_DOLLAR_BANDS,
  FEE_HANDLING,
  SMALL_BUSINESS_STATUSES,
} from '@insolvia-ai/api-client';
import type {
  Address,
  EstimatedCreditorsBand,
  EstimatedDollarBand,
  PetitionBody,
} from '@insolvia-ai/api-client';
import { Checkbox, DateInput, Field, Input, Select, Textarea } from '@insolvia-ai/design-system';
import type { SelectOption } from '@insolvia-ai/design-system';
import { StyleSheet, Text, View } from 'react-native';

import { Heading } from '@/components/heading';
import { labelize } from '@/screens/intake/collections';
import { fontSizes, spacing, useTheme } from '@/theme';

import { formatFormDate, statutoryDates } from './statutory-dates';

/**
 * B101's Part 2–6 case-level answers, as one form (issue #342).
 *
 * ONE RECORD PER CASE BY MEANING, unlike the ten collections in
 * `screens/intake` — churned during intake, then left alone — so this is its
 * own field set rather than a `CollectionSpec` driving the generic
 * `CollectionEditor`. Its save flow lives in `./index.tsx`, matching the
 * debtor form's shape (one continuous record) rather than the collection
 * editor's (discrete rows with an explicit Save per row).
 *
 * Every field is optional, matching the API: intake is progressive and a
 * half-finished record must save. Nothing here blocks a save — the only
 * errors shown are the ones the server sent back, keyed by the same dotted
 * path the field writes to, exactly as `DebtorFields` does.
 */

const YES_NO_OPTIONS = [
  { value: 'yes', label: 'Yes' },
  { value: 'no', label: 'No' },
] as const;

const FEE_HANDLING_OPTIONS: readonly SelectOption[] = FEE_HANDLING.map((value) => ({
  value,
  label: labelize(value),
}));

const DEBT_CHARACTER_OPTIONS: readonly SelectOption[] = DEBT_CHARACTERS.map((value) => ({
  value,
  label: labelize(value),
}));

/**
 * Line 13's four options, with the three chapter 11 ones disabled outside a
 * chapter 11 case — NOT hidden, so the screen still reads like the paper
 * form (issue #342's own instruction). `not_filing_under_chapter_11` is the
 * only option a chapter 7, 12 or 13 case can actually choose.
 */
function smallBusinessOptions(chapter: number): readonly SelectOption[] {
  return SMALL_BUSINESS_STATUSES.map((value) => ({
    value,
    label: labelize(value),
    disabled: value !== 'not_filing_under_chapter_11' && chapter !== 11,
  }));
}

function creditorsBandOptions(): readonly SelectOption[] {
  return ESTIMATED_CREDITORS_BANDS.map((value) => ({ value, label: labelize(value) }));
}

function dollarBandOptions(): readonly SelectOption[] {
  return ESTIMATED_DOLLAR_BANDS.map((value) => ({ value, label: labelize(value) }));
}

/** One estimate line's derived-or-manual state — Part 6's three answers. */
export interface EstimateFieldState<Band extends string> {
  /** From the case's own creditor count or summary totals; undefined when
   * there is nothing yet to derive from. */
  readonly derived: Band | undefined;
  /** Whether the attorney has chosen to enter this one by hand. */
  readonly manual: boolean;
  readonly onManualChange: (next: boolean) => void;
}

export interface PetitionFieldsProps {
  readonly body: PetitionBody;
  readonly onChange: (next: PetitionBody) => void;
  readonly chapter: number;
  /** Server messages, keyed by the field path they belong to. */
  readonly errors: Readonly<Record<string, string>>;
  readonly estimatedCreditors: EstimateFieldState<EstimatedCreditorsBand>;
  readonly estimatedAssets: EstimateFieldState<EstimatedDollarBand>;
  readonly estimatedLiabilities: EstimateFieldState<EstimatedDollarBand>;
}

export function PetitionFields({
  body,
  onChange,
  chapter,
  errors,
  estimatedCreditors,
  estimatedAssets,
  estimatedLiabilities,
}: PetitionFieldsProps) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  const setAddress = (part: keyof Address, value: string) =>
    onChange({
      ...body,
      hazardousProperty: {
        ...body.hazardousProperty,
        address: { ...body.hazardousProperty?.address, [part]: value },
      },
    });

  const dates = statutoryDates(body.expectedFilingDate);

  return (
    <View style={styles.form}>
      <Section title="Filing fee">
        <YesNoLikeSelect
          label="How will the filing fee be handled?"
          path="feeHandling"
          value={body.feeHandling}
          options={FEE_HANDLING_OPTIONS}
          onValueChange={(next) =>
            onChange({ ...body, feeHandling: next as PetitionBody['feeHandling'] })
          }
          errors={errors}
        />
      </Section>

      <Section title="Filing timeline">
        <Field.Root invalid={Boolean(errors.expectedFilingDate)}>
          <Field.Label>Expected filing date</Field.Label>
          <DateInput
            value={body.expectedFilingDate ?? ''}
            onValueChange={(next, status) => {
              if (status === 'incomplete') return;
              onChange({ ...body, expectedFilingDate: next === '' ? undefined : next });
            }}
          />
          <Field.Description>
            A planning date, not a deadline — the two dates below move with it.
          </Field.Description>
          {errors.expectedFilingDate ? (
            <Field.Error match>{errors.expectedFilingDate}</Field.Error>
          ) : null}
        </Field.Root>
        <Text style={[styles.derivedDate, muted]}>
          {dates === null
            ? '§109(h) counseling window: set an expected filing date to see it.'
            : `§109(h) counseling window opens ${formatFormDate(dates.counselingWindowStart)} — the debtor screen collects whether the briefing was completed.`}
        </Text>
      </Section>

      <Section title="Residence">
        <YesNoField
          label="Does the debtor rent their residence?"
          path="rentsResidence"
          value={body.rentsResidence}
          onValueChange={(next) => onChange({ ...body, rentsResidence: next })}
          errors={errors}
        />
        <YesNoField
          label="Has the landlord obtained an eviction judgment against the debtor?"
          path="evictionJudgmentAgainstYou"
          value={body.evictionJudgmentAgainstYou}
          onValueChange={(next) => onChange({ ...body, evictionJudgmentAgainstYou: next })}
          errors={errors}
          disabled={body.rentsResidence !== true}
          disabledReason="Only applies when the debtor rents."
        />
      </Section>

      <Section title="Business filing">
        <Field.Root invalid={Boolean(errors.smallBusinessStatus)}>
          <Field.Label>Small business status</Field.Label>
          <Select
            options={[...smallBusinessOptions(chapter)]}
            value={body.smallBusinessStatus ?? null}
            onValueChange={(next) =>
              onChange({
                ...body,
                smallBusinessStatus: next as PetitionBody['smallBusinessStatus'],
              })
            }
            placeholder="Choose one"
          />
          <Field.Description>
            {chapter === 11
              ? 'Includes the Subchapter V election.'
              : 'The Subchapter V and small-business options only apply to a chapter 11 case.'}
          </Field.Description>
          {errors.smallBusinessStatus ? (
            <Field.Error match>{errors.smallBusinessStatus}</Field.Error>
          ) : null}
        </Field.Root>
      </Section>

      <Section title="Hazardous property">
        <Text style={[styles.help, muted]}>
          Property that needs immediate attention because it poses a threat of imminent and
          identifiable hazard.
        </Text>
        <Field.Root invalid={Boolean(errors['hazardousProperty.description'])}>
          <Field.Label>Describe the property</Field.Label>
          <Textarea
            value={body.hazardousProperty?.description ?? ''}
            onValueChange={(next) =>
              onChange({
                ...body,
                hazardousProperty: { ...body.hazardousProperty, description: next },
              })
            }
          />
          {errors['hazardousProperty.description'] ? (
            <Field.Error match>{errors['hazardousProperty.description']}</Field.Error>
          ) : null}
        </Field.Root>
        <Field.Root invalid={Boolean(errors['hazardousProperty.whyImmediate'])}>
          <Field.Label>Why does it need immediate attention?</Field.Label>
          <Textarea
            value={body.hazardousProperty?.whyImmediate ?? ''}
            onValueChange={(next) =>
              onChange({
                ...body,
                hazardousProperty: { ...body.hazardousProperty, whyImmediate: next },
              })
            }
          />
          {errors['hazardousProperty.whyImmediate'] ? (
            <Field.Error match>{errors['hazardousProperty.whyImmediate']}</Field.Error>
          ) : null}
        </Field.Root>
        <AddressFields
          address={body.hazardousProperty?.address}
          onChange={setAddress}
          errors={errors}
        />
      </Section>

      <Section title="Nature of the debts">
        <Field.Root invalid={Boolean(errors.debtCharacter)}>
          <Field.Label>Are the debts primarily consumer or business debts?</Field.Label>
          <Select
            options={[...DEBT_CHARACTER_OPTIONS]}
            value={body.debtCharacter ?? null}
            onValueChange={(next) =>
              onChange({ ...body, debtCharacter: next as PetitionBody['debtCharacter'] })
            }
            placeholder="Choose one"
          />
          {errors.debtCharacter ? <Field.Error match>{errors.debtCharacter}</Field.Error> : null}
        </Field.Root>
        <Field.Root invalid={Boolean(errors.debtCharacterOther)}>
          <Field.Label>If other, describe</Field.Label>
          <Input
            value={body.debtCharacterOther ?? ''}
            onValueChange={(next) =>
              onChange({ ...body, debtCharacterOther: next === '' ? undefined : next })
            }
            disabled={body.debtCharacter !== 'other'}
            autoCorrect={false}
          />
          <Field.Description>Only applies when "Other" is chosen above.</Field.Description>
          {errors.debtCharacterOther ? (
            <Field.Error match>{errors.debtCharacterOther}</Field.Error>
          ) : null}
        </Field.Root>
        <YesNoField
          label="Chapter 7 only — after exempt property is excluded and administrative expenses paid, will funds be available for distribution to unsecured creditors?"
          path="ch7FundsAvailableForCreditors"
          value={body.ch7FundsAvailableForCreditors}
          onValueChange={(next) => onChange({ ...body, ch7FundsAvailableForCreditors: next })}
          errors={errors}
          disabled={chapter !== 7}
          disabledReason="Only applies to a chapter 7 case."
        />
      </Section>

      <Section title="Estimates (Part 6)">
        <Text style={[styles.help, muted]}>
          Derived from the case's own creditor count and Schedule A/B and D–F totals until you
          choose to enter one by hand — so these three answers cannot silently disagree with the
          schedules.
        </Text>
        <EstimateField
          label="Estimated number of creditors"
          path="estimatedCreditors"
          options={creditorsBandOptions()}
          value={body.estimatedCreditors}
          state={estimatedCreditors}
          onValueChange={(next) =>
            onChange({ ...body, estimatedCreditors: next as PetitionBody['estimatedCreditors'] })
          }
          errors={errors}
        />
        <EstimateField
          label="Estimated assets"
          path="estimatedAssets"
          options={dollarBandOptions()}
          value={body.estimatedAssets}
          state={estimatedAssets}
          onValueChange={(next) =>
            onChange({ ...body, estimatedAssets: next as PetitionBody['estimatedAssets'] })
          }
          errors={errors}
        />
        <EstimateField
          label="Estimated liabilities"
          path="estimatedLiabilities"
          options={dollarBandOptions()}
          value={body.estimatedLiabilities}
          state={estimatedLiabilities}
          onValueChange={(next) =>
            onChange({
              ...body,
              estimatedLiabilities: next as PetitionBody['estimatedLiabilities'],
            })
          }
          errors={errors}
        />
      </Section>
    </View>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <View style={styles.section}>
      <Heading level={3} size="body">
        {title}
      </Heading>
      {children}
    </View>
  );
}

function YesNoField({
  label,
  path,
  value,
  onValueChange,
  errors,
  disabled = false,
  disabledReason,
}: {
  label: string;
  path: string;
  value: boolean | undefined;
  onValueChange: (next: boolean | undefined) => void;
  errors: Readonly<Record<string, string>>;
  disabled?: boolean;
  disabledReason?: string;
}) {
  const message = errors[path];
  return (
    <Field.Root invalid={Boolean(message)}>
      <Field.Label>{label}</Field.Label>
      <Select
        options={[...YES_NO_OPTIONS]}
        value={value === true ? 'yes' : value === false ? 'no' : null}
        onValueChange={(next) =>
          onValueChange(next === 'yes' ? true : next === 'no' ? false : undefined)
        }
        placeholder="Not answered"
        disabled={disabled}
      />
      {disabled && disabledReason ? <Field.Description>{disabledReason}</Field.Description> : null}
      {message ? <Field.Error match>{message}</Field.Error> : null}
    </Field.Root>
  );
}

function YesNoLikeSelect({
  label,
  path,
  value,
  options,
  onValueChange,
  errors,
}: {
  label: string;
  path: string;
  value: string | undefined;
  options: readonly SelectOption[];
  onValueChange: (next: string) => void;
  errors: Readonly<Record<string, string>>;
}) {
  const message = errors[path];
  return (
    <Field.Root invalid={Boolean(message)}>
      <Field.Label>{label}</Field.Label>
      <Select
        options={[...options]}
        value={value ?? null}
        onValueChange={onValueChange}
        placeholder="Choose one"
      />
      {message ? <Field.Error match>{message}</Field.Error> : null}
    </Field.Root>
  );
}

function EstimateField<Band extends string>({
  label,
  path,
  options,
  value,
  state,
  onValueChange,
  errors,
}: {
  label: string;
  path: string;
  options: readonly SelectOption[];
  value: Band | undefined;
  state: EstimateFieldState<Band>;
  onValueChange: (next: string) => void;
  errors: Readonly<Record<string, string>>;
}) {
  const theme = useTheme();
  const message = errors[path];
  const shown = state.manual ? (value ?? null) : (state.derived ?? null);
  return (
    <Field.Root invalid={Boolean(message)}>
      <Field.Label>{label}</Field.Label>
      <Select
        options={[...options]}
        value={shown}
        onValueChange={onValueChange}
        placeholder={state.manual ? 'Choose one' : 'Nothing to derive yet'}
        disabled={!state.manual}
      />
      <View style={styles.manualRow}>
        <Checkbox.Root
          checked={state.manual}
          onCheckedChange={state.onManualChange}
          aria-label={`Enter ${label.toLowerCase()} manually`}
        >
          <Checkbox.Indicator>✓</Checkbox.Indicator>
        </Checkbox.Root>
        <Text
          aria-hidden
          style={[
            styles.manualLabel,
            { color: theme.colors.ink, fontFamily: theme.typography.body },
          ]}
        >
          Enter manually
        </Text>
      </View>
      <Field.Description>
        {state.manual
          ? 'Overriding the derived value — it is saved as entered.'
          : 'Derived from the case; uncheck to see why, check "Enter manually" to override.'}
      </Field.Description>
      {message ? <Field.Error match>{message}</Field.Error> : null}
    </Field.Root>
  );
}

function AddressFields({
  address,
  onChange,
  errors,
}: {
  address: Address | undefined;
  onChange: (part: keyof Address, value: string) => void;
  errors: Readonly<Record<string, string>>;
}) {
  const parts: readonly (readonly [keyof Address, string])[] = [
    ['line1', 'Street'],
    ['line2', 'Apartment, suite or unit'],
    ['city', 'City'],
    ['state', 'State'],
    ['postalCode', 'ZIP code'],
  ];
  return (
    <>
      {parts.map(([part, label]) => {
        const path = `hazardousProperty.address.${part}`;
        const message = errors[path];
        return (
          <Field.Root key={part} invalid={Boolean(message)}>
            <Field.Label>{`Property address — ${label}`}</Field.Label>
            <Input
              value={address?.[part] ?? ''}
              onValueChange={(next) => onChange(part, next)}
              autoCorrect={false}
            />
            {message ? <Field.Error match>{message}</Field.Error> : null}
          </Field.Root>
        );
      })}
    </>
  );
}

const styles = StyleSheet.create({
  derivedDate: { fontSize: fontSizes.label, lineHeight: fontSizes.label * 1.4 },
  form: { gap: spacing.lg },
  help: { fontSize: fontSizes.label },
  manualLabel: { fontSize: fontSizes.label },
  manualRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
  section: { gap: spacing.sm },
});
