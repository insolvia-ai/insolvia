import { COUNSELING_EXEMPTIONS, COUNSELING_STATUSES, VENUE_BASES } from '@insolvia-ai/api-client';
import type { Address, DebtorBody, OtherName, PersonName } from '@insolvia-ai/api-client';
import { Button, DateInput, Field, Select } from '@insolvia-ai/design-system';
import { StyleSheet, Text, View } from 'react-native';

import { fontSizes, spacing, useTheme } from '@/theme';

import { newRowId } from './row-id';

/**
 * B101 Part 1 for one debtor.
 *
 * Every field is optional, matching the API: intake is progressive and a
 * half-finished record must save. So nothing here is marked required and
 * nothing blocks a save — the only errors shown are the ones the server sent
 * back, keyed by the same dotted path the field writes to.
 */

const VENUE_OPTIONS = [
  { value: 'lived_longest_180_days', label: 'Lived here longest in the last 180 days' },
  { value: 'other', label: 'Another reason' },
] as const;

// The four checkboxes of B101 line 15, in the order the form prints them.
const COUNSELING_OPTIONS = [
  { value: 'completed_with_certificate', label: 'Completed — I have the certificate' },
  { value: 'completed_certificate_pending', label: 'Completed — certificate not yet received' },
  { value: 'exigent_circumstances_waiver_requested', label: 'Requesting a 30-day waiver' },
  { value: 'not_required', label: 'Not required' },
] as const;

const EXEMPTION_OPTIONS = [
  { value: 'incapacity', label: 'Incapacity' },
  { value: 'disability', label: 'Disability' },
  { value: 'active_duty', label: 'Active military duty' },
] as const;

/**
 * Narrow the design system's `string` back to the API's union.
 *
 * `Select` is not generic over its option type, so a picker over a closed set
 * hands back a bare string and the model wants the literal. Checking membership
 * against the api-client's OWN runtime array — rather than casting — means the
 * app cannot drift from the API's enum: a value the server would reject cannot
 * reach the request from here. `undefined` clears the field, which is what an
 * unrecognised value should do rather than being stored.
 *
 * Making `Select` generic would remove this, and is the better fix. It is a
 * design-system change, so it needs its own PR and a version bump.
 */
function narrow<T extends string>(allowed: readonly T[], value: string): T | undefined {
  return (allowed as readonly string[]).includes(value) ? (value as T) : undefined;
}

export interface DebtorFieldsProps {
  readonly body: DebtorBody;
  readonly onChange: (next: DebtorBody) => void;
  /** Server messages, keyed by the field path they belong to. */
  readonly errors: Readonly<Record<string, string>>;
  readonly disabled: boolean;
}

export function DebtorFields({ body, onChange, errors, disabled }: DebtorFieldsProps) {
  const theme = useTheme();

  const setName = (part: keyof PersonName, value: string) =>
    onChange({ ...body, name: { ...body.name, [part]: value } });

  const setAddress =
    (which: 'residence_address' | 'mailing_address') => (part: keyof Address, value: string) =>
      onChange({ ...body, [which]: { ...body[which], [part]: value } });

  const setAlias = (id: string, part: keyof OtherName, value: string) =>
    onChange({
      ...body,
      other_names_used: (body.other_names_used ?? []).map((alias) =>
        alias.id === id ? { ...alias, [part]: value } : alias,
      ),
    });

  return (
    <View style={styles.form}>
      <Section title="Name">
        <Text style={[styles.help, { color: theme.colors.muted }]}>
          As it appears on a government-issued picture identification.
        </Text>
        <TextField
          label="First name"
          path="name.given"
          value={body.name?.given}
          onChangeText={(value) => setName('given', value)}
          errors={errors}
          disabled={disabled}
        />
        <TextField
          label="Middle name"
          path="name.middle"
          value={body.name?.middle}
          onChangeText={(value) => setName('middle', value)}
          errors={errors}
          disabled={disabled}
        />
        <TextField
          label="Last name"
          path="name.surname"
          value={body.name?.surname}
          onChangeText={(value) => setName('surname', value)}
          errors={errors}
          disabled={disabled}
        />
        <TextField
          label="Suffix"
          path="name.suffix"
          value={body.name?.suffix}
          onChangeText={(value) => setName('suffix', value)}
          errors={errors}
          disabled={disabled}
        />
      </Section>

      <Section title="Other names used in the last 8 years">
        {(body.other_names_used ?? []).length === 0 ? (
          <Text style={[styles.help, { color: theme.colors.muted }]}>
            None recorded. Add one if this person has filed, worked or held accounts under another
            name.
          </Text>
        ) : null}
        {(body.other_names_used ?? []).map((alias, index) => (
          <View key={alias.id} style={styles.aliasRow}>
            <Text style={[styles.aliasLabel, { color: theme.colors.muted }]}>
              Other name {index + 1}
            </Text>
            <TextField
              label="First name"
              path={`other_names_used[${alias.id}].given`}
              value={alias.given}
              onChangeText={(value) => setAlias(alias.id, 'given', value)}
              errors={errors}
              disabled={disabled}
            />
            <TextField
              label="Last name"
              path={`other_names_used[${alias.id}].surname`}
              value={alias.surname}
              onChangeText={(value) => setAlias(alias.id, 'surname', value)}
              errors={errors}
              disabled={disabled}
            />
            <TextField
              label="Business name"
              path={`other_names_used[${alias.id}].business_name`}
              value={alias.business_name}
              onChangeText={(value) => setAlias(alias.id, 'business_name', value)}
              errors={errors}
              disabled={disabled}
            />
            <Button
              size="lg"
              intent="secondary"
              disabled={disabled}
              onPress={() =>
                onChange({
                  ...body,
                  other_names_used: (body.other_names_used ?? []).filter(
                    (candidate) => candidate.id !== alias.id,
                  ),
                })
              }
            >
              {`Remove other name ${index + 1}`}
            </Button>
          </View>
        ))}
        <Button
          size="lg"
          intent="secondary"
          disabled={disabled}
          onPress={() =>
            // The id is minted HERE, not by the server — see row-id.ts. Without
            // one the API cannot accept the row at all.
            onChange({
              ...body,
              other_names_used: [...(body.other_names_used ?? []), { id: newRowId() }],
            })
          }
        >
          Add another name
        </Button>
      </Section>

      <Section title="Where the debtor lives">
        <AddressFields
          which="residence_address"
          address={body.residence_address}
          onChange={setAddress('residence_address')}
          errors={errors}
          disabled={disabled}
        />
      </Section>

      <Section title="Mailing address">
        <Text style={[styles.help, { color: theme.colors.muted }]}>
          Only if it is different from the address above.
        </Text>
        <AddressFields
          which="mailing_address"
          address={body.mailing_address}
          onChange={setAddress('mailing_address')}
          errors={errors}
          disabled={disabled}
        />
      </Section>

      <Section title="Contact">
        <TextField
          label="Phone"
          path="phone"
          value={body.phone}
          onChangeText={(value) => onChange({ ...body, phone: value })}
          errors={errors}
          disabled={disabled}
        />
        <TextField
          label="Mobile"
          path="mobile"
          value={body.mobile}
          onChangeText={(value) => onChange({ ...body, mobile: value })}
          errors={errors}
          disabled={disabled}
        />
        <TextField
          label="Email"
          path="email"
          value={body.email}
          onChangeText={(value) => onChange({ ...body, email: value })}
          errors={errors}
          disabled={disabled}
        />
      </Section>

      <Section title="Why this district">
        <Field.Root invalid={Boolean(errors['venue.basis'])}>
          <Field.Label>Reason for filing here</Field.Label>
          <Select
            options={VENUE_OPTIONS}
            value={body.venue?.basis ?? null}
            onValueChange={(next) =>
              onChange({
                ...body,
                venue: { ...body.venue, basis: narrow(VENUE_BASES, next) },
              })
            }
            placeholder="Choose a reason"
            disabled={disabled}
          />
          {errors['venue.basis'] ? <Field.Error match>{errors['venue.basis']}</Field.Error> : null}
        </Field.Root>
        {body.venue?.basis === 'other' ? (
          <TextField
            label="Explain"
            path="venue.explanation"
            value={body.venue?.explanation}
            onChangeText={(value) =>
              onChange({ ...body, venue: { ...body.venue, explanation: value } })
            }
            errors={errors}
            disabled={disabled}
          />
        ) : null}
      </Section>

      <Section title="Credit counseling">
        <Field.Root invalid={Boolean(errors['credit_counseling.status'])}>
          <Field.Label>Briefing status</Field.Label>
          <Select
            options={COUNSELING_OPTIONS}
            value={body.credit_counseling?.status ?? null}
            onValueChange={(next) =>
              onChange({
                ...body,
                credit_counseling: {
                  ...body.credit_counseling,
                  status: narrow(COUNSELING_STATUSES, next),
                },
              })
            }
            placeholder="Choose a status"
            disabled={disabled}
          />
          {errors['credit_counseling.status'] ? (
            <Field.Error match>{errors['credit_counseling.status']}</Field.Error>
          ) : null}
        </Field.Root>
        {/* Only the "not required" branch has grounds to give, and the form
            only prints them there. */}
        {body.credit_counseling?.status === 'not_required' ? (
          <Field.Root invalid={Boolean(errors['credit_counseling.exemption_reason'])}>
            <Field.Label>Why not</Field.Label>
            <Select
              options={EXEMPTION_OPTIONS}
              value={body.credit_counseling?.exemption_reason ?? null}
              onValueChange={(next) =>
                onChange({
                  ...body,
                  credit_counseling: {
                    ...body.credit_counseling,
                    exemption_reason: narrow(COUNSELING_EXEMPTIONS, next),
                  },
                })
              }
              placeholder="Choose a reason"
              disabled={disabled}
            />
          </Field.Root>
        ) : null}
      </Section>

      <Section title="Signature">
        <Field.Root invalid={Boolean(errors.signed_at)}>
          <Field.Label>Date signed</Field.Label>
          <DateInput
            value={body.signed_at ?? ''}
            onValueChange={(next) => onChange({ ...body, signed_at: next })}
            disabled={disabled}
          />
          <Field.Description>The date on the petition, not today&apos;s date.</Field.Description>
          {errors.signed_at ? <Field.Error match>{errors.signed_at}</Field.Error> : null}
        </Field.Root>
      </Section>
    </View>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  const theme = useTheme();
  return (
    <View style={styles.section}>
      {/* A plain Text, not a Heading: these sit under the screen's <h2> and a
          third level here would be structure invented for visual weight. The
          group is named by aria-label instead, which is what a screen reader
          announces on entering it. */}
      <View aria-label={title} style={styles.sectionBody}>
        <Text style={[styles.sectionTitle, { color: theme.colors.ink }]}>{title}</Text>
        {children}
      </View>
    </View>
  );
}

function AddressFields({
  which,
  address,
  onChange,
  errors,
  disabled,
}: {
  which: 'residence_address' | 'mailing_address';
  address: Address | undefined;
  onChange: (part: keyof Address, value: string) => void;
  errors: Readonly<Record<string, string>>;
  disabled: boolean;
}) {
  const parts: readonly (readonly [keyof Address, string])[] = [
    ['line1', 'Street'],
    ['line2', 'Apartment, suite or unit'],
    ['city', 'City'],
    ['state', 'State'],
    ['postal_code', 'ZIP code'],
  ];
  return (
    <>
      {parts.map(([part, label]) => (
        <TextField
          key={part}
          label={label}
          path={`${which}.${part}`}
          value={address?.[part]}
          onChangeText={(value) => onChange(part, value)}
          errors={errors}
          disabled={disabled}
        />
      ))}
    </>
  );
}

function TextField({
  label,
  path,
  value,
  onChangeText,
  errors,
  disabled,
}: {
  label: string;
  path: string;
  value: string | undefined;
  onChangeText: (value: string) => void;
  errors: Readonly<Record<string, string>>;
  disabled: boolean;
}) {
  const message = errors[path];
  return (
    <Field.Root invalid={Boolean(message)}>
      <Field.Label>{label}</Field.Label>
      <Field.Control
        value={value ?? ''}
        onChangeText={onChangeText}
        editable={!disabled}
        autoCorrect={false}
      />
      {message ? <Field.Error match>{message}</Field.Error> : null}
    </Field.Root>
  );
}

const styles = StyleSheet.create({
  aliasLabel: { fontSize: fontSizes.label, fontWeight: '600' },
  aliasRow: { gap: spacing.sm },
  form: { gap: spacing.lg },
  help: { fontSize: fontSizes.label },
  section: { gap: spacing.sm },
  sectionBody: { gap: spacing.sm },
  sectionTitle: { fontSize: fontSizes.body, fontWeight: '600' },
});
