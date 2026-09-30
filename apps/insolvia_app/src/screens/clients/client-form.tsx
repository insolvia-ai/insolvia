import type { Address, FirmClient, FirmClientDraft } from '@insolvia-ai/api-client';
import { DateInput, Field, Input } from '@insolvia-ai/design-system';
import { StyleSheet, Text, View } from 'react-native';

import { Heading } from '@/components/heading';
import type { HeadingLevel } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * The editable half of a firm client (ADR 0022), as the form holds it: every
 * box a string, blank meaning absent. The tax ID is NOT here and cannot be —
 * the API refuses one on this record; its last four arrive read-only once a
 * case seals the full value (#382).
 */
export interface ClientFormState {
  readonly given: string;
  readonly middle: string;
  readonly surname: string;
  readonly suffix: string;
  readonly dateOfBirth: string;
  readonly phone: string;
  readonly mobile: string;
  readonly email: string;
  readonly residence: AddressState;
  readonly mailing: AddressState;
  readonly leadSource: string;
  readonly referredBy: string;
  readonly firstRetainedAt: string;
}

interface AddressState {
  readonly line1: string;
  readonly line2: string;
  readonly city: string;
  readonly state: string;
  readonly postalCode: string;
  readonly county: string;
}

const EMPTY_ADDRESS: AddressState = {
  line1: '',
  line2: '',
  city: '',
  state: '',
  postalCode: '',
  county: '',
};

export const EMPTY_CLIENT_FORM: ClientFormState = {
  given: '',
  middle: '',
  surname: '',
  suffix: '',
  dateOfBirth: '',
  phone: '',
  mobile: '',
  email: '',
  residence: EMPTY_ADDRESS,
  mailing: EMPTY_ADDRESS,
  leadSource: '',
  referredBy: '',
  firstRetainedAt: '',
};

function addressState(address: Address | undefined): AddressState {
  return {
    line1: address?.line1 ?? '',
    line2: address?.line2 ?? '',
    city: address?.city ?? '',
    state: address?.state ?? '',
    postalCode: address?.postal_code ?? '',
    county: address?.county ?? '',
  };
}

/** The form, filled from a stored client — the start of an edit. */
export function formFromClient(client: FirmClient): ClientFormState {
  return {
    given: client.name.given ?? '',
    middle: client.name.middle ?? '',
    surname: client.name.surname ?? '',
    suffix: client.name.suffix ?? '',
    dateOfBirth: client.date_of_birth ?? '',
    phone: client.phone ?? '',
    mobile: client.mobile ?? '',
    email: client.email ?? '',
    residence: addressState(client.residence_address),
    mailing: addressState(client.mailing_address),
    leadSource: client.lead_source ?? '',
    referredBy: client.referred_by ?? '',
    firstRetainedAt: client.first_retained_at ?? '',
  };
}

function blankToUndefined(value: string): string | undefined {
  const trimmed = value.trim();
  return trimmed === '' ? undefined : trimmed;
}

function addressOrUndefined(address: AddressState): Address | undefined {
  const built: Address = {
    line1: blankToUndefined(address.line1),
    line2: blankToUndefined(address.line2),
    city: blankToUndefined(address.city),
    state: blankToUndefined(address.state),
    postal_code: blankToUndefined(address.postalCode),
    county: blankToUndefined(address.county),
  };
  return Object.values(built).some((value) => value !== undefined) ? built : undefined;
}

/**
 * The WHOLE-RECORD body `POST`/`PUT /v1/firm/clients` takes.
 *
 * `stored` is the record being edited, and it matters: a PUT clears anything
 * it omits, and this form does not edit `other_names_used` (an alias list is
 * the intake's job, with its own row ids) — so an edit carries the stored
 * aliases through untouched rather than erasing them by not showing them.
 */
export function requestFromForm(form: ClientFormState, stored?: FirmClient): FirmClientDraft {
  return {
    name: {
      given: blankToUndefined(form.given),
      middle: blankToUndefined(form.middle),
      surname: blankToUndefined(form.surname),
      suffix: blankToUndefined(form.suffix),
    },
    other_names_used: stored?.other_names_used,
    date_of_birth: blankToUndefined(form.dateOfBirth),
    residence_address: addressOrUndefined(form.residence),
    mailing_address: addressOrUndefined(form.mailing),
    phone: blankToUndefined(form.phone),
    mobile: blankToUndefined(form.mobile),
    email: blankToUndefined(form.email),
    lead_source: blankToUndefined(form.leadSource),
    referred_by: blankToUndefined(form.referredBy),
    first_retained_at: blankToUndefined(form.firstRetainedAt),
  };
}

/**
 * The client's details as boxes — shared by "Add client" and the record's
 * edit mode, so the two cannot disagree about what a client is.
 *
 * Errors are the SERVER's, keyed by its field paths (`name`,
 * `date_of_birth`, `residence_address.postal_code`, …) and rendered on the
 * box they name (ADR 0001): the rule lives in `insolvia_core.firm_clients`,
 * not in a second copy here. `name` — "a surname or a first name" — is
 * shown under the surname, the box a person reaches for first.
 *
 * `headingLevel` is the level of this form's SECTION headings, which depends
 * on the page around it: sections of `/clients/new` sit under its `<h1>`,
 * sections of an edit sit under the record's "Details" `<h2>`.
 */
export function ClientForm({
  value,
  onChange,
  errors,
  headingLevel,
}: {
  value: ClientFormState;
  onChange: (next: ClientFormState) => void;
  errors: Readonly<Record<string, string>>;
  headingLevel: HeadingLevel;
}) {
  const theme = useTheme();
  const set = <K extends keyof ClientFormState>(key: K, next: ClientFormState[K]) =>
    onChange({ ...value, [key]: next });

  const text = (
    field: string,
    label: string,
    current: string,
    update: (next: string) => void,
    type?: 'email' | 'tel',
  ) => (
    <Field.Root name={field} invalid={Boolean(errors[field])}>
      <Field.Label>{label}</Field.Label>
      <Input
        value={current}
        onValueChange={update}
        autoCorrect={false}
        {...(type === undefined ? {} : { type })}
      />
      {errors[field] ? <Field.Error match>{errors[field]}</Field.Error> : null}
    </Field.Root>
  );

  const date = (field: string, label: string, current: string, update: (next: string) => void) => (
    <Field.Root name={field} invalid={Boolean(errors[field])}>
      <Field.Label>{label}</Field.Label>
      <DateInput
        value={current}
        // `incomplete` is ignored: a half-typed date reports '' and writing
        // that through would clear the box's value on the first backspace.
        onValueChange={(next, status) => {
          if (status === 'incomplete') return;
          update(next);
        }}
      />
      {errors[field] ? <Field.Error match>{errors[field]}</Field.Error> : null}
    </Field.Root>
  );

  const address = (
    root: 'residence_address' | 'mailing_address',
    key: 'residence' | 'mailing',
    title: string,
  ) => {
    const current = value[key];
    const put = (part: keyof AddressState) => (next: string) =>
      set(key, { ...current, [part]: next });
    // The mailing boxes say so in their NAMES: two boxes both labelled
    // "City" are one name for two controls, and a screen reader lists
    // fields by name, not by the heading above them.
    // An initialism ("ZIP") keeps its capitals.
    const label = (words: string) =>
      key !== 'mailing'
        ? words
        : `Mailing ${/^[A-Z][a-z]/.test(words) ? words.charAt(0).toLowerCase() + words.slice(1) : words}`;
    return (
      <View style={styles.section}>
        <Heading level={headingLevel} size="body">
          {title}
        </Heading>
        {text(`${root}.line1`, label('Street address'), current.line1, put('line1'))}
        {text(`${root}.line2`, label('Apartment, suite or unit'), current.line2, put('line2'))}
        <View style={styles.row}>
          <View style={styles.grow}>
            {text(`${root}.city`, label('City'), current.city, put('city'))}
          </View>
          <View style={styles.narrow}>
            {text(`${root}.state`, label('State'), current.state, put('state'))}
          </View>
          <View style={styles.narrow}>
            {text(`${root}.postal_code`, label('ZIP code'), current.postalCode, put('postalCode'))}
          </View>
        </View>
        {root === 'residence_address'
          ? text(`${root}.county`, 'County', current.county, put('county'))
          : null}
      </View>
    );
  };

  return (
    <View style={styles.form}>
      <View style={styles.section}>
        <Heading level={headingLevel} size="body">
          Name
        </Heading>
        <Text
          style={[styles.help, { color: theme.colors.muted, fontFamily: theme.typography.body }]}
        >
          As it appears on their ID. A last name or a first name is required; the rest can wait.
        </Text>
        <View style={styles.row}>
          <View style={styles.grow}>
            {text('name.given', 'First name', value.given, (next) => set('given', next))}
          </View>
          <View style={styles.grow}>
            {text('name.middle', 'Middle name', value.middle, (next) => set('middle', next))}
          </View>
        </View>
        <View style={styles.row}>
          <View style={styles.grow}>
            {/* The server keys "a name is required" as `name`, so it lands here. */}
            <Field.Root name="name" invalid={Boolean(errors.name ?? errors['name.surname'])}>
              <Field.Label>Last name</Field.Label>
              <Input
                value={value.surname}
                onValueChange={(next) => set('surname', next)}
                autoCorrect={false}
              />
              {errors.name || errors['name.surname'] ? (
                <Field.Error match>{errors.name ?? errors['name.surname']}</Field.Error>
              ) : null}
            </Field.Root>
          </View>
          <View style={styles.narrow}>
            {text('name.suffix', 'Suffix', value.suffix, (next) => set('suffix', next))}
          </View>
        </View>
        {date('date_of_birth', 'Date of birth', value.dateOfBirth, (next) =>
          set('dateOfBirth', next),
        )}
      </View>

      <View style={styles.section}>
        <Heading level={headingLevel} size="body">
          Contact
        </Heading>
        {text('email', 'Email', value.email, (next) => set('email', next), 'email')}
        <View style={styles.row}>
          <View style={styles.grow}>
            {text('phone', 'Phone', value.phone, (next) => set('phone', next), 'tel')}
          </View>
          <View style={styles.grow}>
            {text('mobile', 'Mobile', value.mobile, (next) => set('mobile', next), 'tel')}
          </View>
        </View>
      </View>

      {address('residence_address', 'residence', 'Where they live')}
      {address('mailing_address', 'mailing', 'Mailing address, if different')}

      <View style={styles.section}>
        <Heading level={headingLevel} size="body">
          How they came to the firm
        </Heading>
        <View style={styles.row}>
          <View style={styles.grow}>
            {text('lead_source', 'Lead source', value.leadSource, (next) =>
              set('leadSource', next),
            )}
          </View>
          <View style={styles.grow}>
            {text('referred_by', 'Referred by', value.referredBy, (next) =>
              set('referredBy', next),
            )}
          </View>
        </View>
        {date('first_retained_at', 'First retained', value.firstRetainedAt, (next) =>
          set('firstRetainedAt', next),
        )}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  form: { gap: spacing.lg },
  grow: { flexBasis: 200, flexGrow: 1 },
  help: { fontSize: fontSizes.label, lineHeight: fontSizes.label * 1.5 },
  narrow: { flexBasis: 120, flexGrow: 0 },
  row: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.md },
  section: { gap: spacing.md },
});
