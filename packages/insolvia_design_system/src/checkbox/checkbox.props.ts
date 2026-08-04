// SHARED — imports `react`, `../lib/controllable`, and
// `../checkbox-group/checkbox-group.props` only. No react-dom, no
// react-native. This is the checkbox's entire behavior model: standalone
// controlled/uncontrolled checked state via `useControllableState`, OR — when
// rendered inside a `CheckboxGroup.Root` and given a `value` — checked state
// and toggling derived from the group instead. Both leaves consume it
// verbatim; they differ only in the elements and a11y surface they render.
import * as React from 'react';

import { useControllableState } from '../lib/controllable';
import { useCheckboxGroupContext } from '../checkbox-group/checkbox-group.props';

export interface CheckboxIndicatorContextValue {
  checked: boolean;
  indeterminate: boolean;
}

export const CheckboxRootContext = React.createContext<CheckboxIndicatorContextValue | null>(null);

export function useCheckboxRootContext(part: string): CheckboxIndicatorContextValue {
  const ctx = React.useContext(CheckboxRootContext);
  if (!ctx) throw new Error(`Checkbox.${part} must be rendered inside <Checkbox.Root>`);
  return ctx;
}

export interface CheckboxRootOwnProps {
  /** Controlled checked state. Omit for uncontrolled (see `defaultChecked`). */
  checked?: boolean | undefined;
  /** Checked state on first render. Uncontrolled; omit for an unchecked start. */
  defaultChecked?: boolean | undefined;
  onCheckedChange?: ((checked: boolean) => void) | undefined;
  /** Visual third state — layered over `checked`, not a value of its own. */
  indeterminate?: boolean | undefined;
  disabled?: boolean | undefined;
  /**
   * This checkbox's membership value in an enclosing `CheckboxGroup.Root`.
   * When both a `value` AND an ancestor group are present, the group's
   * context OVERRIDES standalone `checked`/`defaultChecked`/`onCheckedChange`
   * entirely — checked is derived from group membership, and toggling
   * mutates the group's array via `onValueChange`, not local state.
   */
  value?: string | undefined;
}

export interface CheckboxState {
  checked: boolean;
  indeterminate: boolean;
  disabled: boolean;
  toggle: () => void;
}

/**
 * Standalone-or-grouped checked state, described in the comment above. A
 * group only takes over when it exists AND `value` is given — an ungrouped
 * checkbox, or a grouped one without a `value`, is always standalone.
 */
export function useCheckboxState(
  checked: boolean | undefined,
  defaultChecked: boolean | undefined,
  onCheckedChange: ((checked: boolean) => void) | undefined,
  indeterminate: boolean,
  disabled: boolean,
  value: string | undefined,
): CheckboxState {
  const group = useCheckboxGroupContext();
  const [standaloneChecked, setStandaloneChecked] = useControllableState<boolean>(
    checked,
    defaultChecked ?? false,
    onCheckedChange,
  );

  const inGroup = group !== null && value !== undefined;
  const resolvedChecked = inGroup ? group.value.includes(value) : standaloneChecked;
  const resolvedDisabled = disabled || (inGroup && group.disabled);

  const toggle = React.useCallback(() => {
    if (resolvedDisabled) return;
    if (inGroup) {
      group.toggle(value);
    } else {
      setStandaloneChecked(!standaloneChecked);
    }
  }, [resolvedDisabled, inGroup, group, value, standaloneChecked, setStandaloneChecked]);

  return {
    checked: resolvedChecked,
    indeterminate,
    disabled: resolvedDisabled,
    toggle,
  };
}
