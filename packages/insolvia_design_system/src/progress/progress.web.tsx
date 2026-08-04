// WEB LEAF — plain React DOM + Tailwind. Root owns the value/max and exposes
// them through context; Track is pure surface, Indicator reads the context to
// size its own width. `aria-valuenow` is set to `undefined` (not omitted via a
// spread trick) when indeterminate — React drops an `undefined` attribute from
// the DOM, and this is the same idiom `describedBy`/etc. use elsewhere in the
// package.
import * as React from 'react';

import { cn } from '../lib/cn';
import {
  ProgressRootContext,
  progressPercent,
  useProgressRootContext,
  type ProgressRootOwnProps,
} from './progress.props';

export interface ProgressRootProps
  extends Omit<React.ComponentPropsWithoutRef<'div'>, 'value'>,
    ProgressRootOwnProps {}

const ProgressRoot = React.forwardRef<HTMLDivElement, ProgressRootProps>(
  ({ value = null, max = 100, className, children, ...props }, ref) => {
    const indeterminate = value === null;
    const percent = progressPercent(value, max);

    return (
      <div
        ref={ref}
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={max}
        aria-valuenow={indeterminate ? undefined : value}
        className={cn('relative w-full', className)}
        {...props}
      >
        <ProgressRootContext.Provider value={{ value, max, percent }}>
          {children}
        </ProgressRootContext.Provider>
      </div>
    );
  },
);
ProgressRoot.displayName = 'Progress.Root';

const ProgressTrack = React.forwardRef<HTMLDivElement, React.ComponentPropsWithoutRef<'div'>>(
  ({ className, ...props }, ref) => (
    <div
      ref={ref}
      className={cn('h-2 w-full overflow-hidden rounded-pill bg-surface-alt', className)}
      {...props}
    />
  ),
);
ProgressTrack.displayName = 'Progress.Track';

const ProgressIndicator = React.forwardRef<HTMLDivElement, React.ComponentPropsWithoutRef<'div'>>(
  ({ className, style, ...props }, ref) => {
    const { value, percent } = useProgressRootContext('Indicator');
    const indeterminate = value === null;

    return (
      <div
        ref={ref}
        data-state={indeterminate ? 'indeterminate' : 'progressing'}
        className={cn(
          'h-full rounded-pill bg-primary transition-[width]',
          indeterminate && 'w-1/3 animate-pulse',
          className,
        )}
        style={indeterminate ? style : { width: `${percent}%`, ...style }}
        {...props}
      />
    );
  },
);
ProgressIndicator.displayName = 'Progress.Indicator';

export const Progress = {
  Root: ProgressRoot,
  Track: ProgressTrack,
  Indicator: ProgressIndicator,
};
