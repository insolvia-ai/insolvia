// SHARED — imports `react` (+ `../lib/controllable`) only. No react-dom, no
// react-native. This is the switch's entire behavior model: the on/off value,
// how a press toggles it, and the checked flag Thumb needs to position
// itself. Both leaves consume this verbatim; they differ only in the elements
// and a11y wiring rendered around it.
import * as React from 'react';

import { useControllableState } from '../lib/controllable';

export interface SwitchContextValue {
  checked: boolean;
  disabled: boolean;
}

export const SwitchContext = React.createContext<SwitchContextValue | null>(null);

export function useSwitchContext(part: string): SwitchContextValue {
  const ctx = React.useContext(SwitchContext);
  if (!ctx) throw new Error(`Switch.${part} must be rendered inside <Switch.Root>`);
  return ctx;
}

// The `| undefined` on each member is for `exactOptionalPropertyTypes` (on in
// tsconfig.base.json): the web leaf's own props interface extends
// `React.ComponentPropsWithoutRef<'button'>`, whose `disabled` is typed
// `boolean | undefined` — extending both interfaces with a bare-optional
// `disabled` here is a type conflict, the same gotcha Button's
// `ButtonClassOptions` documents.
export interface SwitchRootOwnProps {
  /** Whether the switch is on, parent-owned. Omit for uncontrolled mode. */
  checked?: boolean | undefined;
  /** Whether the switch is on on first render. Uncontrolled; defaults to off. */
  defaultChecked?: boolean | undefined;
  /** Fires with the state the switch *wants* to be in, in both modes. */
  onCheckedChange?: ((checked: boolean) => void) | undefined;
  disabled?: boolean | undefined;
}

export interface SwitchState extends SwitchContextValue {
  toggle: () => void;
}

/**
 * The on/off state machine — pure React, identical on both platforms. A
 * disabled switch never toggles, in either mode.
 */
export function useSwitchState(
  checked: boolean | undefined,
  defaultChecked: boolean,
  onCheckedChange: ((checked: boolean) => void) | undefined,
  disabled: boolean,
): SwitchState {
  const [current, setCurrent] = useControllableState<boolean>(checked, defaultChecked, onCheckedChange);

  const toggle = React.useCallback(() => {
    if (disabled) return;
    setCurrent(!current);
  }, [current, disabled, setCurrent]);

  return React.useMemo(
    () => ({ checked: current, disabled, toggle }),
    [current, disabled, toggle],
  );
}
