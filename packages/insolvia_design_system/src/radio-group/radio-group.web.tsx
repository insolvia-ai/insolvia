// WEB LEAF — plain React DOM, WAI-ARIA radiogroup pattern. Shares its
// selection state with the native leaf via radio-group.props; what lives here
// is the DOM: role="radiogroup"/role="radio", aria-checked, roving tabIndex,
// and arrow-key focus management, using the same data-* +
// closest()/querySelectorAll() querying idiom accordion.web.tsx uses for its
// own keyboard nav — Item queries its root by `[data-radiogroup-root]` and
// its siblings by `[data-radio-item]`, never by ref-passing.
import * as React from 'react';

import { cn } from '../lib/cn';
import { disabledStyles, focusRing } from '../lib/styles';
import {
  RadioGroupItemContext,
  RadioGroupRootContext,
  radioItemTabIndex,
  useRadioGroupItemContext,
  useRadioGroupItemState,
  useRadioGroupRootContext,
  useRadioGroupState,
  type RadioGroupRootOwnProps,
} from './radio-group.props';

// `defaultValue` is Omit-ed because React's own `HTMLAttributes` already
// declares it (as a form-control value type), and the group's string meaning
// must win.
export interface RadioGroupRootProps
  extends Omit<React.ComponentPropsWithoutRef<'div'>, 'defaultValue'>, RadioGroupRootOwnProps {}

const RadioGroupRoot = React.forwardRef<HTMLDivElement, RadioGroupRootProps>(
  ({ className, value, defaultValue, onValueChange, disabled = false, children, ...props }, ref) => {
    const ctx = useRadioGroupState(value, defaultValue, onValueChange, disabled);
    return (
      <RadioGroupRootContext.Provider value={ctx}>
        <div
          ref={ref}
          role="radiogroup"
          data-radiogroup-root=""
          aria-disabled={disabled ? true : undefined}
          className={cn('flex flex-col gap-sm', className)}
          {...props}
        >
          {children}
        </div>
      </RadioGroupRootContext.Provider>
    );
  },
);
RadioGroupRoot.displayName = 'RadioGroup.Root';

// `value` is Omit-ed because `ButtonHTMLAttributes` already declares it (as
// the form-submission value), and the item's identity-string meaning must win.
export interface RadioGroupItemProps extends Omit<React.ComponentPropsWithoutRef<'button'>, 'value'> {
  /** This item's identity — the value the group takes on when it is picked. */
  value: string;
}

const RadioGroupItem = React.forwardRef<HTMLButtonElement, RadioGroupItemProps>(
  ({ className, value, disabled: itemDisabled = false, onClick, onKeyDown, children, ...props }, forwardedRef) => {
    const root = useRadioGroupRootContext('Item');
    const itemState = useRadioGroupItemState(value, root.value, root.disabled, itemDisabled);

    const innerRef = React.useRef<HTMLButtonElement | null>(null);
    const setRefs = React.useCallback(
      (node: HTMLButtonElement | null) => {
        innerRef.current = node;
        if (typeof forwardedRef === 'function') forwardedRef(node);
        else if (forwardedRef) forwardedRef.current = node;
      },
      [forwardedRef],
    );

    // Whether this item is first in DOM order — a fact only the DOM has;
    // read once on mount with the same closest()/querySelectorAll() idiom
    // the keyboard handler below uses. Items reordering after mount is out
    // of scope, same as accordion's own trigger set.
    const [isFirst, setIsFirst] = React.useState(false);
    React.useEffect(() => {
      const rootEl = innerRef.current?.closest<HTMLElement>('[data-radiogroup-root]');
      const first = rootEl?.querySelector<HTMLButtonElement>('[data-radio-item]');
      setIsFirst(first === innerRef.current);
    }, []);

    const tabIndex = radioItemTabIndex(itemState.checked, root.value !== undefined, isFirst);

    const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
      onKeyDown?.(event);
      if (event.defaultPrevented) return;
      const nav = ['ArrowDown', 'ArrowRight', 'ArrowUp', 'ArrowLeft'];
      if (!nav.includes(event.key)) return;
      const rootEl = event.currentTarget.closest<HTMLElement>('[data-radiogroup-root]');
      if (!rootEl) return;
      event.preventDefault();
      const items = Array.from(
        rootEl.querySelectorAll<HTMLButtonElement>('[data-radio-item]:not(:disabled)'),
      );
      if (items.length === 0) return;
      const i = items.indexOf(event.currentTarget);
      const forward = event.key === 'ArrowDown' || event.key === 'ArrowRight';
      const target = forward
        ? items[(i + 1) % items.length]
        : items[(i - 1 + items.length) % items.length];
      const nextValue = target?.dataset.radioValue;
      if (target && nextValue !== undefined) {
        target.focus();
        root.setValue(nextValue);
      }
    };

    return (
      <button
        ref={setRefs}
        type="button"
        role="radio"
        data-radio-item=""
        data-radio-value={value}
        aria-checked={itemState.checked}
        disabled={itemState.disabled}
        tabIndex={tabIndex}
        onClick={(event) => {
          onClick?.(event);
          if (!event.defaultPrevented && !itemState.disabled) root.setValue(value);
        }}
        onKeyDown={handleKeyDown}
        className={cn(
          'flex h-5 w-5 shrink-0 cursor-pointer items-center justify-center rounded-pill border border-line bg-card',
          focusRing,
          disabledStyles,
          'aria-checked:border-primary',
          className,
        )}
        {...props}
      >
        <RadioGroupItemContext.Provider value={itemState}>{children}</RadioGroupItemContext.Provider>
      </button>
    );
  },
);
RadioGroupItem.displayName = 'RadioGroup.Item';

const RadioGroupIndicator = React.forwardRef<HTMLSpanElement, React.ComponentPropsWithoutRef<'span'>>(
  ({ className, children, ...props }, ref) => {
    const { checked } = useRadioGroupItemContext('Indicator');
    if (!checked) return null;
    return (
      <span
        ref={ref}
        data-radio-indicator=""
        className={cn('h-2.5 w-2.5 rounded-pill bg-primary', className)}
        {...props}
      >
        {children}
      </span>
    );
  },
);
RadioGroupIndicator.displayName = 'RadioGroup.Indicator';

export const RadioGroup = {
  Root: RadioGroupRoot,
  Item: RadioGroupItem,
  Indicator: RadioGroupIndicator,
};
