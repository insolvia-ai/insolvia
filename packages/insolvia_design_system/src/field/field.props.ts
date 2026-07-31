// SHARED — `react` only (context + useId). No react-dom, no react-native.
// What genuinely composes across platforms for a field is small and lives here:
// the id scheme, the context shape, and the `aria-describedby` string rule.
// Everything that renders (label/input vs Text/TextInput) and the child-presence
// scan (which keys off each leaf's OWN subcomponents) stays in the leaves — see
// the ergonomics note in index.ts.
import * as React from 'react';

export interface FieldContextValue {
  labelId: string;
  controlId: string;
  describedBy: string | undefined;
  invalid: boolean;
  name: string | undefined;
  descriptionId: string;
  errorId: string;
}

export const FieldContext = React.createContext<FieldContextValue | null>(null);

export function useFieldContext(part: string): FieldContextValue {
  const ctx = React.useContext(FieldContext);
  if (!ctx) throw new Error(`Field.${part} must be rendered inside <Field.Root>`);
  return ctx;
}

export interface FieldIds {
  labelId: string;
  controlId: string;
  descriptionId: string;
  errorId: string;
}

/**
 * Stable id set for one field. Shared derivation, no DOM. It carries ids for
 * BOTH association directions because the platforms wire opposite ways: the
 * web leaf points the label at the control (`htmlFor={controlId}`), the
 * native leaf points the control back at the label
 * (`aria-labelledby={labelId}` — the pair react-native-web emits as a
 * correctly associated label/input).
 */
export function useFieldIds(): FieldIds {
  const id = React.useId();
  return {
    labelId: `${id}-label`,
    controlId: `${id}-control`,
    descriptionId: `${id}-description`,
    errorId: `${id}-error`,
  };
}

/**
 * Compose `aria-describedby` from ONLY the parts actually rendered — a dangling
 * reference to an absent id is its own a11y defect. Pure string rule, shared by
 * both leaves; computed during render so it is present in server-rendered HTML.
 */
export function composeDescribedBy(
  ids: FieldIds,
  hasDescription: boolean,
  hasError: boolean,
): string | undefined {
  return (
    [hasDescription ? ids.descriptionId : null, hasError ? ids.errorId : null]
      .filter(Boolean)
      .join(' ') || undefined
  );
}

export interface FieldRootOwnProps {
  /** The form control's `name`, applied to the control unless it sets its own. */
  name?: string;
  /** Marks the control invalid: sets `aria-invalid` and the danger border. */
  invalid?: boolean;
}
