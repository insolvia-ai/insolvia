// WEB LEAF — plain React DOM + Tailwind, WAI-ARIA switch pattern. Shares its
// on/off state with the native leaf via switch.props; what lives here is the
// DOM: role="switch"/aria-checked on a real <button>, and a data-state
// attribute the Thumb reads via a Tailwind data-attribute variant to
// translate itself, rather than branching in JS.
import * as React from 'react';

import { cn } from '../lib/cn';
import { disabledStyles, focusRing } from '../lib/styles';
import {
  SwitchContext,
  useSwitchContext,
  useSwitchState,
  type SwitchRootOwnProps,
} from './switch.props';

// `defaultChecked`/`defaultValue` are Omit-ed because React's own
// `HTMLAttributes` already declares them (as native form-control types), and
// the switch's boolean on/off meaning must win.
export interface SwitchRootProps
  extends
    Omit<React.ComponentPropsWithoutRef<'button'>, 'defaultChecked' | 'defaultValue'>,
    SwitchRootOwnProps {}

const SwitchRoot = React.forwardRef<HTMLButtonElement, SwitchRootProps>(
  (
    {
      className,
      checked,
      defaultChecked = false,
      onCheckedChange,
      disabled = false,
      onClick,
      children,
      ...props
    },
    ref,
  ) => {
    const state = useSwitchState(checked, defaultChecked, onCheckedChange, disabled);

    return (
      <SwitchContext.Provider value={{ checked: state.checked, disabled: state.disabled }}>
        <button
          ref={ref}
          type="button"
          role="switch"
          aria-checked={state.checked}
          disabled={disabled}
          data-state={state.checked ? 'checked' : 'unchecked'}
          onClick={(event) => {
            onClick?.(event);
            if (!event.defaultPrevented) state.toggle();
          }}
          className={cn(
            'relative inline-flex h-6 w-11 shrink-0 cursor-pointer items-center rounded-pill transition-colors',
            'data-[state=unchecked]:bg-line data-[state=checked]:bg-primary',
            focusRing,
            disabledStyles,
            className,
          )}
          {...props}
        >
          {children}
        </button>
      </SwitchContext.Provider>
    );
  },
);
SwitchRoot.displayName = 'Switch.Root';

const SwitchThumb = React.forwardRef<HTMLSpanElement, React.ComponentPropsWithoutRef<'span'>>(
  ({ className, ...props }, ref) => {
    const { checked } = useSwitchContext('Thumb');
    return (
      <span
        ref={ref}
        data-state={checked ? 'checked' : 'unchecked'}
        className={cn(
          'pointer-events-none inline-block h-5 w-5 translate-x-0.5 rounded-pill bg-card shadow-sm transition-transform',
          'data-[state=checked]:translate-x-[22px]',
          className,
        )}
        {...props}
      />
    );
  },
);
SwitchThumb.displayName = 'Switch.Thumb';

export const Switch = {
  Root: SwitchRoot,
  Thumb: SwitchThumb,
};
