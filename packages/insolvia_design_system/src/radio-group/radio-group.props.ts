// SHARED — imports `react` (+ `../lib/controllable`) only. No react-dom, no
// react-native. This is the radio group's entire behavior model: which value
// is selected, how selecting one mutates it, and each item's derived
// checked/disabled state. Both leaves consume this verbatim. What does NOT
// live here: roving tabIndex and arrow-key focus are DOM/keyboard concepts —
// only the web leaf has a notion of tab order, so `radio-group.web.tsx` owns
// that querying (same data-* + closest()/querySelectorAll() idiom
// accordion.web.tsx uses) and only asks this module for the pure arithmetic
// (`radioItemTabIndex`) once it has the DOM fact ("am I first?").
import * as React from 'react';

import { useControllableState } from '../lib/controllable';

export interface RadioGroupRootContextValue {
  value: string | undefined;
  setValue: (value: string) => void;
  disabled: boolean;
}

export const RadioGroupRootContext = React.createContext<RadioGroupRootContextValue | null>(null);

export function useRadioGroupRootContext(part: string): RadioGroupRootContextValue {
  const ctx = React.useContext(RadioGroupRootContext);
  if (!ctx) throw new Error(`RadioGroup.${part} must be rendered inside <RadioGroup.Root>`);
  return ctx;
}

export interface RadioGroupItemContextValue {
  value: string;
  checked: boolean;
  disabled: boolean;
}

export const RadioGroupItemContext = React.createContext<RadioGroupItemContextValue | null>(null);

export function useRadioGroupItemContext(part: string): RadioGroupItemContextValue {
  const ctx = React.useContext(RadioGroupItemContext);
  if (!ctx) throw new Error(`RadioGroup.${part} must be rendered inside <RadioGroup.Item>`);
  return ctx;
}

export interface RadioGroupRootOwnProps {
  /** The selected item's value, parent-owned. Omit for uncontrolled mode. */
  value?: string | undefined;
  /** The selected item's value on first render. Uncontrolled; omit for no initial selection. */
  defaultValue?: string | undefined;
  /** Fires with the value the group *wants* to select, in both modes. */
  onValueChange?: ((value: string) => void) | undefined;
  /** Disables every item; an item may also disable itself individually. */
  disabled?: boolean | undefined;
}

/**
 * The selection state machine — pure React, identical on both platforms.
 * Exactly one value may be selected at a time.
 */
export function useRadioGroupState(
  value: string | undefined,
  defaultValue: string | undefined,
  onValueChange: ((value: string) => void) | undefined,
  disabled: boolean,
): RadioGroupRootContextValue {
  // `useControllableState<string | undefined>` types its change callback as
  // `(next: string | undefined) => void` — the group's own `value` may be
  // undefined (nothing selected yet). `onValueChange`, though, is a
  // selection callback: it only ever means "the group wants to select X",
  // never "the group wants to become unselected" (there is no gesture that
  // deselects a radio group), so it is typed to take a defined `string`.
  // This wrapper is the seam between those two honest types; `setValue`
  // below only ever calls `setCurrent` with a defined value, so the
  // `undefined` branch is unreachable at runtime, not just filtered here.
  const handleChange = React.useCallback(
    (next: string | undefined) => {
      if (next !== undefined) onValueChange?.(next);
    },
    [onValueChange],
  );

  const [current, setCurrent] = useControllableState<string | undefined>(
    value,
    defaultValue,
    handleChange,
  );

  const setValue = React.useCallback((next: string) => setCurrent(next), [setCurrent]);

  return React.useMemo(
    () => ({ value: current, setValue, disabled }),
    [current, setValue, disabled],
  );
}

/** Derive one item's checked/disabled state from the group's current value. */
export function useRadioGroupItemState(
  itemValue: string,
  groupValue: string | undefined,
  groupDisabled: boolean,
  itemDisabled: boolean,
): RadioGroupItemContextValue {
  return {
    value: itemValue,
    checked: groupValue === itemValue,
    disabled: groupDisabled || itemDisabled,
  };
}

/**
 * WAI-ARIA roving-tabindex rule for a radio group: the checked item is always
 * tabbable; when nothing is checked, exactly one item is — the first, so Tab
 * lands somewhere sensible and arrow keys pick up selection from there.
 * `isFirst` is a DOM fact (order in the rendered tree) the web leaf supplies;
 * this function is the pure, testable arithmetic on top of it.
 */
export function radioItemTabIndex(
  checked: boolean,
  hasGroupSelection: boolean,
  isFirst: boolean,
): 0 | -1 {
  if (checked) return 0;
  if (!hasGroupSelection && isFirst) return 0;
  return -1;
}
