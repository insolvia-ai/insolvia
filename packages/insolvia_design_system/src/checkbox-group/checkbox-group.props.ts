// SHARED — imports `react` and `../lib/controllable` only. No react-dom, no
// react-native. The group's entire behavior model: a controlled-or-uncontrolled
// array of checked values, and a toggle that adds/removes one member. Both
// leaves consume it verbatim; `checkbox.props.ts` reads this context (one-way
// dependency — the group never imports anything from `../checkbox`) so a
// `Checkbox.Root` given a `value` prop can derive its checked state from, and
// toggle into, an ancestor group instead of its own standalone state.
import * as React from 'react';

import { useControllableState } from '../lib/controllable';

export interface CheckboxGroupContextValue {
  /** The group's current member values. */
  value: string[];
  /** Add `itemValue` if absent, remove it if present. */
  toggle: (itemValue: string) => void;
  /** Group-level disable, ANDed with each checkbox's own `disabled`. */
  disabled: boolean;
}

export const CheckboxGroupContext = React.createContext<CheckboxGroupContextValue | null>(null);

/**
 * Returns the enclosing group's context, or `null` when there isn't one — a
 * `Checkbox.Root` is valid both standalone and inside a `CheckboxGroup.Root`,
 * so unlike Accordion/Field's `use*Context`, this does NOT throw outside its
 * provider.
 */
export function useCheckboxGroupContext(): CheckboxGroupContextValue | null {
  return React.useContext(CheckboxGroupContext);
}

export interface CheckboxGroupRootOwnProps {
  /** The group's member values. Controlled: parent owns the array. */
  value?: string[] | undefined;
  /** Member values on first render. Uncontrolled; omit for an empty start. */
  defaultValue?: string[] | undefined;
  onValueChange?: ((value: string[]) => void) | undefined;
  /** Disables every checkbox that reads this group's context. */
  disabled?: boolean | undefined;
}

/**
 * The group's state machine — pure React, identical on both platforms.
 * Builds on `useControllableState` for the controlled/uncontrolled split;
 * `toggle` derives the next array from the current one, which the setter
 * (by contract) never needs — the caller always has it in scope.
 */
export function useCheckboxGroupState(
  value: string[] | undefined,
  defaultValue: string[] | undefined,
  onValueChange: ((value: string[]) => void) | undefined,
  disabled: boolean,
): CheckboxGroupContextValue {
  const [current, setCurrent] = useControllableState<string[]>(
    value,
    defaultValue ?? [],
    onValueChange,
  );

  const toggle = React.useCallback(
    (itemValue: string) => {
      const next = current.includes(itemValue)
        ? current.filter((v) => v !== itemValue)
        : [...current, itemValue];
      setCurrent(next);
    },
    [current, setCurrent],
  );

  return React.useMemo(() => ({ value: current, toggle, disabled }), [current, toggle, disabled]);
}
