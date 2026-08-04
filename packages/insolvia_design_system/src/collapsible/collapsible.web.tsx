// WEB LEAF — plain React DOM, WAI-ARIA disclosure pattern (a single-item
// accordion). Shares its entire state model with the native leaf via
// collapsible.props; what lives here is the DOM: button/aria-expanded/
// aria-controls wiring, and the panel's grid-rows collapse + `inert` while
// closed — the exact idiom accordion.web.tsx uses for its panel, reused
// verbatim since Collapsible IS a single-item accordion.
import * as React from 'react';

import { cn } from '../lib/cn';
import { disabledStyles, focusRing } from '../lib/styles';
import {
  CollapsibleRootContext,
  useCollapsibleRootContext,
  useCollapsibleState,
  type CollapsibleRootOwnProps,
} from './collapsible.props';

export interface CollapsibleRootProps
  extends React.ComponentPropsWithoutRef<'div'>, CollapsibleRootOwnProps {}

const CollapsibleRoot = React.forwardRef<HTMLDivElement, CollapsibleRootProps>(
  (
    { className, open, defaultOpen = false, onOpenChange, disabled = false, children, ...props },
    ref,
  ) => {
    const ctx = useCollapsibleState(open, defaultOpen, onOpenChange, disabled);
    return (
      <CollapsibleRootContext.Provider value={ctx}>
        <div
          ref={ref}
          data-collapsible-root=""
          className={cn('flex flex-col', className)}
          {...props}
        >
          {children}
        </div>
      </CollapsibleRootContext.Provider>
    );
  },
);
CollapsibleRoot.displayName = 'Collapsible.Root';

const CollapsibleTrigger = React.forwardRef<
  HTMLButtonElement,
  React.ComponentPropsWithoutRef<'button'>
>(({ className, onClick, disabled, ...props }, ref) => {
  const {
    toggle,
    open,
    disabled: rootDisabled,
    triggerId,
    panelId,
  } = useCollapsibleRootContext('Trigger');
  const isDisabled = disabled ?? rootDisabled;

  return (
    <button
      ref={ref}
      type="button"
      id={triggerId}
      disabled={isDisabled}
      aria-expanded={open}
      aria-controls={panelId}
      onClick={(event) => {
        onClick?.(event);
        if (!event.defaultPrevented) toggle();
      }}
      className={cn(
        'flex cursor-pointer items-center gap-sm py-sm text-left font-body text-sm font-medium text-ink',
        focusRing,
        disabledStyles,
        className,
      )}
      {...props}
    />
  );
});
CollapsibleTrigger.displayName = 'Collapsible.Trigger';

const CollapsiblePanel = React.forwardRef<HTMLDivElement, React.ComponentPropsWithoutRef<'div'>>(
  ({ className, children, ...props }, ref) => {
    const { open, triggerId, panelId } = useCollapsibleRootContext('Panel');
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
CollapsiblePanel.displayName = 'Collapsible.Panel';

export const Collapsible = {
  Root: CollapsibleRoot,
  Trigger: CollapsibleTrigger,
  Panel: CollapsiblePanel,
};
