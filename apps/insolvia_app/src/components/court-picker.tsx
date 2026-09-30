import type { CourtRegistry } from '@insolvia-ai/api-client';
import { Field, Select } from '@insolvia-ai/design-system';

/**
 * The court and division pickers, fed by the registry (`GET /v1/courts`,
 * issue #360). Two `Select`s rather than one flattened list: a district has
 * up to seven divisions and the court is the fact the preparer knows first —
 * the division follows from the debtor's county, which the registry also
 * carries and a later screen can use to suggest it.
 *
 * A component rather than part of a screen because two screens use it: the
 * form that opens a case (`OpenCaseForm`) and the firm's case defaults
 * (`screens/firm`).
 */
export function CourtPicker({
  registry,
  court,
  division,
  onCourtChange,
  onDivisionChange,
  errors,
  courtLabel = 'Court',
  divisionLabel = 'Division',
  courtField = 'court',
  divisionField = 'division',
}: {
  registry: CourtRegistry;
  court: string | null;
  division: string | null;
  onCourtChange: (court: string) => void;
  onDivisionChange: (division: string) => void;
  errors: Readonly<Record<string, string>>;
  courtLabel?: string;
  divisionLabel?: string;
  /** The error-map keys, which differ between a case (`court`) and a firm default (`defaultCourt`). */
  courtField?: string;
  divisionField?: string;
}) {
  const district = registry.districts.find((d) => d.code === court);
  const courtOptions = registry.districts.map((d) => ({ value: d.code, label: d.name }));
  const divisionOptions = (district?.divisions ?? []).map((d) => ({
    value: d.code,
    label: d.name,
  }));
  return (
    <>
      <Field.Root name={courtField} invalid={Boolean(errors[courtField])}>
        <Field.Label>{courtLabel}</Field.Label>
        <Select
          options={courtOptions}
          value={court}
          onValueChange={onCourtChange}
          placeholder="Choose a court"
        />
        <Field.Description>
          The bankruptcy court this case will be filed in, from the court registry.
        </Field.Description>
        {errors[courtField] ? <Field.Error match>{errors[courtField]}</Field.Error> : null}
      </Field.Root>
      <Field.Root name={divisionField} invalid={Boolean(errors[divisionField])}>
        <Field.Label>{divisionLabel}</Field.Label>
        <Select
          options={divisionOptions}
          value={division}
          onValueChange={onDivisionChange}
          placeholder={district === undefined ? 'Choose a court first' : 'Choose a division'}
          disabled={district === undefined}
        />
        {errors[divisionField] ? <Field.Error match>{errors[divisionField]}</Field.Error> : null}
      </Field.Root>
    </>
  );
}
