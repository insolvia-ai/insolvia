// WEB LEAF — plain React DOM + Tailwind. A single `<div role="separator">`,
// no compound parts. `aria-orientation` is set explicitly for both values
// (not just the non-default one) so the attribute is always present for a
// test/consumer to assert on, matching Base UI's own `data-orientation`
// convention of always reflecting the current orientation.
import * as React from 'react';

import { cn } from '../lib/cn';
import type { SeparatorOwnProps } from './separator.props';

export interface SeparatorProps
  extends Omit<React.ComponentPropsWithoutRef<'div'>, 'role'>,
    SeparatorOwnProps {}

const orientationClass: Record<'horizontal' | 'vertical', string> = {
  horizontal: 'h-px w-full',
  vertical: 'w-px self-stretch',
};

export const Separator = React.forwardRef<HTMLDivElement, SeparatorProps>(
  ({ className, orientation = 'horizontal', ...props }, ref) => (
    <div
      ref={ref}
      role="separator"
      aria-orientation={orientation}
      className={cn('shrink-0 bg-line', orientationClass[orientation], className)}
      {...props}
    />
  ),
);
Separator.displayName = 'Separator';
