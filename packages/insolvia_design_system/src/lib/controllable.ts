// SHARED — imports `react` only; no react-dom, no react-native (ESLint-fenced,
// same as every src/lib module). One state convention for every component that
// takes input: an optional controlled prop wins when provided, otherwise an
// internal useState seeded from the default, and the change callback fires in
// both modes. Props-module state hooks build on this so no component invents
// its own controlled/uncontrolled split.
import * as React from 'react';

/**
 * Controlled-or-uncontrolled state. `controlled === undefined` means
 * uncontrolled: state lives here, seeded from `defaultValue`. A defined
 * `controlled` value wins on every render and internal state is bypassed —
 * the parent owns the value and must re-render with a new one. `onChange`
 * fires on every set in both modes, with the value the component *wants*,
 * which in controlled mode the parent may still decline to apply.
 *
 * The setter takes a plain value, not an updater function — derive the next
 * value from the current one in the caller, which always has it in scope.
 */
export function useControllableState<T>(
  controlled: T | undefined,
  defaultValue: T,
  onChange?: ((next: T) => void) | undefined,
): [T, (next: T) => void] {
  const [internal, setInternal] = React.useState<T>(defaultValue);
  const isControlled = controlled !== undefined;
  const value = isControlled ? controlled : internal;

  const onChangeRef = React.useRef(onChange);
  React.useEffect(() => {
    onChangeRef.current = onChange;
  });

  const setValue = React.useCallback(
    (next: T) => {
      if (!isControlled) setInternal(next);
      onChangeRef.current?.(next);
    },
    [isControlled],
  );

  return [value, setValue];
}
