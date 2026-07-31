// WEB LEAF — plain React DOM + Tailwind, WAI-ARIA accordion. Shares its entire
// state model with the native leaf via accordion.props; what lives here is the
// DOM: heading/button/region elements, aria-expanded/-controls/-labelledby,
// arrow-key roving focus, and the grid-rows height animation.
import * as React from 'react';

import { cn } from '../lib/cn';
import { disabledStyles, focusRing } from '../lib/styles';
import {
  AccordionItemContext,
  AccordionRootContext,
  useAccordionItemState,
  useAccordionRootContext,
  useAccordionItemContext,
  useAccordionState,
  type AccordionRootOwnProps,
} from './accordion.props';

// `defaultValue` is Omit-ed from the div props because React's own
// `HTMLAttributes` already declares it (as a form-control value type), and the
// accordion's string[] meaning must win.
export interface AccordionRootProps
  extends Omit<React.ComponentPropsWithoutRef<'div'>, 'defaultValue'>, AccordionRootOwnProps {}

const AccordionRoot = React.forwardRef<HTMLDivElement, AccordionRootProps>(
  ({ className, defaultValue, openMultiple = true, children, ...props }, ref) => {
    const ctx = useAccordionState(defaultValue, openMultiple);
    return (
      <AccordionRootContext.Provider value={ctx}>
        <div ref={ref} data-accordion-root="" className={cn('flex flex-col', className)} {...props}>
          {children}
        </div>
      </AccordionRootContext.Provider>
    );
  },
);
AccordionRoot.displayName = 'Accordion.Root';

export interface AccordionItemProps extends React.ComponentPropsWithoutRef<'div'> {
  /** Stable identity of this item, used as its open/closed key. */
  value: string;
}

const AccordionItem = React.forwardRef<HTMLDivElement, AccordionItemProps>(
  ({ className, value, children, ...props }, ref) => {
    const { isOpen } = useAccordionRootContext('Item');
    const ctx = useAccordionItemState(value, isOpen);
    return (
      <AccordionItemContext.Provider value={ctx}>
        <div ref={ref} className={cn('border-b border-line', className)} {...props}>
          {children}
        </div>
      </AccordionItemContext.Provider>
    );
  },
);
AccordionItem.displayName = 'Accordion.Item';

const AccordionHeader = React.forwardRef<HTMLHeadingElement, React.ComponentPropsWithoutRef<'h3'>>(
  ({ className, ...props }, ref) => <h3 ref={ref} className={cn('flex', className)} {...props} />,
);
AccordionHeader.displayName = 'Accordion.Header';

const AccordionTrigger = React.forwardRef<
  HTMLButtonElement,
  React.ComponentPropsWithoutRef<'button'>
>(({ className, onClick, onKeyDown, ...props }, ref) => {
  const { toggle } = useAccordionRootContext('Trigger');
  const { value, open, triggerId, panelId } = useAccordionItemContext('Trigger');

  const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
    onKeyDown?.(event);
    if (event.defaultPrevented) return;
    const nav = ['ArrowDown', 'ArrowUp', 'Home', 'End'];
    if (!nav.includes(event.key)) return;
    const root = event.currentTarget.closest<HTMLElement>('[data-accordion-root]');
    if (!root) return;
    event.preventDefault();
    const triggers = Array.from(
      root.querySelectorAll<HTMLButtonElement>('[data-accordion-trigger]'),
    );
    const i = triggers.indexOf(event.currentTarget);
    const target =
      event.key === 'ArrowDown'
        ? triggers[(i + 1) % triggers.length]
        : event.key === 'ArrowUp'
          ? triggers[(i - 1 + triggers.length) % triggers.length]
          : event.key === 'Home'
            ? triggers[0]
            : triggers[triggers.length - 1];
    target?.focus();
  };

  return (
    <button
      ref={ref}
      type="button"
      id={triggerId}
      data-accordion-trigger=""
      aria-expanded={open}
      aria-controls={panelId}
      onClick={(event) => {
        onClick?.(event);
        if (!event.defaultPrevented) toggle(value);
      }}
      onKeyDown={handleKeyDown}
      className={cn(
        'flex flex-1 cursor-pointer items-center justify-between gap-md py-md text-left font-body text-base font-medium text-ink',
        focusRing,
        disabledStyles,
        className,
      )}
      {...props}
    />
  );
});
AccordionTrigger.displayName = 'Accordion.Trigger';

const AccordionPanel = React.forwardRef<HTMLDivElement, React.ComponentPropsWithoutRef<'div'>>(
  ({ className, children, ...props }, ref) => {
    const { open, triggerId, panelId } = useAccordionItemContext('Panel');
    return (
      <div
        ref={ref}
        id={panelId}
        role="region"
        aria-labelledby={triggerId}
        inert={!open}
        className={cn(
          'grid transition-[grid-template-rows] duration-200 ease-out motion-reduce:transition-none',
          open ? 'grid-rows-[1fr]' : 'grid-rows-[0fr]',
        )}
        {...props}
      >
        <div className="overflow-hidden">
          <div className={cn('font-body text-sm text-muted', className)}>{children}</div>
        </div>
      </div>
    );
  },
);
AccordionPanel.displayName = 'Accordion.Panel';

export const Accordion = {
  Root: AccordionRoot,
  Item: AccordionItem,
  Header: AccordionHeader,
  Trigger: AccordionTrigger,
  Panel: AccordionPanel,
};
