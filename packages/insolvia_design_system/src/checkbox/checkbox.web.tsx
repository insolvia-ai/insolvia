// WEB LEAF — plain React DOM + Tailwind. Shares its entire state model with
// the native leaf via checkbox.props; what lives here is the DOM: a
// `<button role="checkbox">` (Space/Enter toggle for free via native button
// semantics — no keydown handler needed), `aria-checked` incl. `"mixed"`, and
// the Indicator's checked/indeterminate-gated children.
//
// No hidden `<input type="checkbox">`: real `<form>` submission integration
// (a `name`/`value` pair posting with the form) needs more than a trivial
// `display:none` input — synced `checked`/`indeterminate` on every state
// change, `form`/`required` plumbing, and a native-input change listener kept
// in lockstep with `toggle()`. That is real, untested surface for a control
// this app doesn't currently submit through a native form, so it is left out
// rather than shipped half-built; see the PR description for this note.
import * as React from 'react';

import { cn } from '../lib/cn';
import { disabledStyles, focusRing } from '../lib/styles';
import {
  CheckboxRootContext,
  useCheckboxRootContext,
  useCheckboxState,
  type CheckboxIndicatorContextValue,
  type CheckboxRootOwnProps,
} from './checkbox.props';

// `value` is Omit-ed from the button props because React's own
// `ButtonHTMLAttributes` already declares it (as a form-submission value,
// `string | number | readonly string[]`), and the checkbox's group-membership
// string meaning must win.
export interface CheckboxRootProps
  extends
    Omit<
      React.ComponentPropsWithoutRef<'button'>,
      'checked' | 'defaultChecked' | 'onChange' | 'value'
    >,
    CheckboxRootOwnProps {}

const CheckboxRoot = React.forwardRef<HTMLButtonElement, CheckboxRootProps>(
  (
    {
      className,
      checked,
      defaultChecked,
      onCheckedChange,
      indeterminate = false,
      disabled = false,
      value,
      onClick,
      children,
      ...props
    },
    ref,
  ) => {
    const state = useCheckboxState(
      checked,
      defaultChecked,
      onCheckedChange,
      indeterminate,
      disabled,
      value,
    );

    const ctx: CheckboxIndicatorContextValue = {
      checked: state.checked,
      indeterminate: state.indeterminate,
    };

    return (
      <CheckboxRootContext.Provider value={ctx}>
        <button
          ref={ref}
          type="button"
          role="checkbox"
          aria-checked={state.indeterminate ? 'mixed' : state.checked}
          disabled={state.disabled}
          onClick={(event) => {
            onClick?.(event);
            if (!event.defaultPrevented) state.toggle();
          }}
          className={cn(
            'flex h-5 w-5 shrink-0 items-center justify-center rounded-sm border border-line bg-card',
            'aria-checked:border-primary aria-checked:bg-primary',
            'aria-[checked=mixed]:border-primary aria-[checked=mixed]:bg-primary',
            focusRing,
            disabledStyles,
            className,
          )}
          {...props}
        >
          {children}
        </button>
      </CheckboxRootContext.Provider>
    );
  },
);
CheckboxRoot.displayName = 'Checkbox.Root';

const CheckboxIndicator = React.forwardRef<HTMLSpanElement, React.ComponentPropsWithoutRef<'span'>>(
  ({ className, children, ...props }, ref) => {
    const { checked, indeterminate } = useCheckboxRootContext('Indicator');
    if (!checked && !indeterminate) return null;
    return (
      <span
        ref={ref}
        className={cn(
          'pointer-events-none flex items-center justify-center text-primary-text',
          className,
        )}
        {...props}
      >
        {children}
      </span>
    );
  },
);
CheckboxIndicator.displayName = 'Checkbox.Indicator';

export const Checkbox = {
  Root: CheckboxRoot,
  Indicator: CheckboxIndicator,
};
