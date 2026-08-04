// SHARED — imports `react`, `../lib/controllable`, and
// `../toggle-group/toggle-group.props` only. No react-dom, no react-native.
// This is the toggle's entire behavior model: standalone controlled/
// uncontrolled pressed state via `useControllableState`, OR — when rendered
// inside a `ToggleGroup.Root` and given a `value` — pressed state and
// toggling derived from the group instead. Both leaves consume it verbatim;
// they differ only in the elements and a11y surface they render. Mirrors
// `checkbox.props.ts`'s standalone-or-grouped shape.
import * as React from 'react';

import { useControllableState } from '../lib/controllable';
import { useToggleGroupContext } from '../toggle-group/toggle-group.props';

export interface ToggleOwnProps {
  /** Controlled pressed state. Omit for uncontrolled (see `defaultPressed`). */
  pressed?: boolean | undefined;
  /** Pressed state on first render. Uncontrolled; omit for an unpressed start. */
  defaultPressed?: boolean | undefined;
  onPressedChange?: ((pressed: boolean) => void) | undefined;
  disabled?: boolean | undefined;
  /**
   * This toggle's membership value in an enclosing `ToggleGroup.Root`. When
   * both a `value` AND an ancestor group are present, the group's context
   * OVERRIDES standalone `pressed`/`defaultPressed`/`onPressedChange`
   * entirely — pressed is derived from group membership, and toggling
   * mutates the group's array via `onValueChange`, not local state. Omit
   * for a standalone toggle, even one rendered inside a group.
   */
  value?: string | undefined;
}

export interface ToggleState {
  pressed: boolean;
  disabled: boolean;
  /** Flip the pressed state — standalone state, or the enclosing group's selection. */
  toggle: () => void;
}

/**
 * Standalone-or-grouped pressed state, described in the comment above. A
 * group only takes over when it exists AND `value` is given — an ungrouped
 * toggle, or a grouped one without a `value`, is always standalone.
 */
export function useToggleState(
  pressed: boolean | undefined,
  defaultPressed: boolean | undefined,
  onPressedChange: ((pressed: boolean) => void) | undefined,
  disabled: boolean | undefined,
  value: string | undefined,
): ToggleState {
  const group = useToggleGroupContext();
  const [standalonePressed, setStandalonePressed] = useControllableState<boolean>(
    pressed,
    defaultPressed ?? false,
    onPressedChange,
  );

  const inGroup = group !== null && value !== undefined;
  const resolvedPressed = inGroup ? group.value.includes(value) : standalonePressed;
  const resolvedDisabled = disabled || (inGroup && group.disabled);

  const toggle = React.useCallback(() => {
    if (resolvedDisabled) return;
    if (inGroup) {
      group.toggle(value);
    } else {
      setStandalonePressed(!standalonePressed);
    }
  }, [resolvedDisabled, inGroup, group, value, standalonePressed, setStandalonePressed]);

  return {
    pressed: resolvedPressed,
    disabled: resolvedDisabled,
    toggle,
  };
}
