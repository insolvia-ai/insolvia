import type { Accordion as AccordionNamespace } from '@base-ui/react/accordion';

import * as React from 'react';
import { Accordion as BaseAccordion } from '@base-ui/react/accordion';

import { cn } from '../../lib/cn';
import { disabledStyles, focusRing } from '../../lib/styles';

function AccordionRoot({ className, ...props }: AccordionNamespace.Root.Props) {
  return <BaseAccordion.Root className={cn('flex flex-col', className)} {...props} />;
}
AccordionRoot.displayName = 'Accordion.Root';

const AccordionItem = React.forwardRef<HTMLDivElement, AccordionNamespace.Item.Props>(
  ({ className, ...props }, ref) => (
    <BaseAccordion.Item ref={ref} className={cn('border-b border-line', className)} {...props} />
  ),
);
AccordionItem.displayName = 'Accordion.Item';

const AccordionHeader = React.forwardRef<HTMLHeadingElement, AccordionNamespace.Header.Props>(
  ({ className, ...props }, ref) => (
    <BaseAccordion.Header ref={ref} className={cn('flex', className)} {...props} />
  ),
);
AccordionHeader.displayName = 'Accordion.Header';

const AccordionTrigger = React.forwardRef<HTMLElement, AccordionNamespace.Trigger.Props>(
  ({ className, ...props }, ref) => (
    <BaseAccordion.Trigger
      ref={ref}
      className={cn(
        'flex flex-1 cursor-pointer items-center justify-between gap-md py-md text-left font-body text-base font-medium text-ink',
        focusRing,
        disabledStyles,
        className,
      )}
      {...props}
    />
  ),
);
AccordionTrigger.displayName = 'Accordion.Trigger';

const AccordionPanel = React.forwardRef<HTMLDivElement, AccordionNamespace.Panel.Props>(
  ({ className, ...props }, ref) => (
    <BaseAccordion.Panel
      ref={ref}
      className={cn(
        'h-[var(--accordion-panel-height)] overflow-hidden font-body text-sm text-muted transition-[height]',
        className,
      )}
      {...props}
    />
  ),
);
AccordionPanel.displayName = 'Accordion.Panel';

export const Accordion = {
  Root: AccordionRoot,
  Item: AccordionItem,
  Header: AccordionHeader,
  Trigger: AccordionTrigger,
  Panel: AccordionPanel,
};
