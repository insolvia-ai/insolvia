// WEB LEAF — plain React DOM + Tailwind. Shares its entire pressed-state
// model with the native leaf via toggle.props; what lives here is the DOM: a
// real `<button aria-pressed>`, and — when this toggle sits inside a
// ToggleGroup — ArrowLeft/ArrowRight roving focus between sibling toggles,
// the same `data-*` + closest()/querySelectorAll() idiom accordion.web.tsx
// uses for its trigger navigation. Activation (click/Space/Enter) is left to
// the native button; only focus movement is handled here.
import * as React from 'react';

import { cn } from '../lib/cn';
import { disabledStyles, focusRing } from '../lib/styles';
import { useToggleState, type ToggleOwnProps } from './toggle.props';

// `value` is Omit-ed from the button props because HTMLButtonElement's own
// `value` is a form-submission string and this component's `value` means
// "this toggle's identity within an enclosing ToggleGroup" — same underlying
// type, different meaning, so the component's own must win outright (the
// AccordionRootProps `defaultValue` Omit is the same idiom).
export interface ToggleProps
  extends Omit<React.ComponentPropsWithoutRef<'button'>, 'value'>,
    ToggleOwnProps {}

export const Toggle = React.forwardRef<HTMLButtonElement, ToggleProps>(
  (
    {
      className,
      onClick,
      onKeyDown,
      pressed,
      defaultPressed,
      onPressedChange,
      disabled,
      value,
      type,
      ...props
    },
    ref,
  ) => {
    const state = useToggleState(pressed, defaultPressed, onPressedChange, disabled, value);

    const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
      onKeyDown?.(event);
      if (event.defaultPrevented) return;
      const nav = ['ArrowLeft', 'ArrowRight'];
      if (!nav.includes(event.key)) return;
      const root = event.currentTarget.closest<HTMLElement>('[data-toggle-group-root]');
      if (!root) return;
      event.preventDefault();
      const items = Array.from(root.querySelectorAll<HTMLButtonElement>('[data-toggle-item]'));
      const i = items.indexOf(event.currentTarget);
      const target =
        event.key === 'ArrowRight'
          ? items[(i + 1) % items.length]
          : items[(i - 1 + items.length) % items.length];
      target?.focus();
    };

    return (
      <button
        ref={ref}
        type={type ?? 'button'}
        data-toggle-item=""
        aria-pressed={state.pressed}
        disabled={state.disabled}
        value={value}
        onClick={(event) => {
          onClick?.(event);
          if (!event.defaultPrevented) state.toggle();
        }}
        onKeyDown={handleKeyDown}
        className={cn(
          'inline-flex cursor-pointer items-center justify-center gap-xs rounded-md px-md py-sm font-body text-sm font-medium transition-colors',
          focusRing,
          disabledStyles,
          state.pressed
            ? 'bg-primary text-primary-text hover:bg-primary-hover active:bg-primary-active'
            : 'bg-transparent text-ink hover:bg-surface-alt active:bg-line',
          className,
        )}
        {...props}
      />
    );
  },
);
Toggle.displayName = 'Toggle';
