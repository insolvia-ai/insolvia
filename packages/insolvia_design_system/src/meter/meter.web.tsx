// WEB LEAF — plain React DOM + Tailwind. Visually identical to Progress
// (same Track/Indicator classes) but semantically a `role="meter"`, always
// determinate, and ranged over [min, max] instead of [0, max].
import * as React from 'react';

import { cn } from '../lib/cn';
import {
  MeterRootContext,
  meterPercent,
  useMeterRootContext,
  type MeterRootOwnProps,
} from './meter.props';

export interface MeterRootProps
  extends Omit<React.ComponentPropsWithoutRef<'div'>, 'value'>, MeterRootOwnProps {}

const MeterRoot = React.forwardRef<HTMLDivElement, MeterRootProps>(
  ({ value, min = 0, max = 100, className, children, ...props }, ref) => {
    const percent = meterPercent(value, min, max);

    return (
      <div
        ref={ref}
        role="meter"
        aria-valuemin={min}
        aria-valuemax={max}
        aria-valuenow={value}
        className={cn('relative w-full', className)}
        {...props}
      >
        <MeterRootContext.Provider value={{ value, min, max, percent }}>
          {children}
        </MeterRootContext.Provider>
      </div>
    );
  },
);
MeterRoot.displayName = 'Meter.Root';

const MeterTrack = React.forwardRef<HTMLDivElement, React.ComponentPropsWithoutRef<'div'>>(
  ({ className, ...props }, ref) => (
    <div
      ref={ref}
      className={cn('h-2 w-full overflow-hidden rounded-pill bg-surface-alt', className)}
      {...props}
    />
  ),
);
MeterTrack.displayName = 'Meter.Track';

const MeterIndicator = React.forwardRef<HTMLDivElement, React.ComponentPropsWithoutRef<'div'>>(
  ({ className, style, ...props }, ref) => {
    const { percent } = useMeterRootContext('Indicator');

    return (
      <div
        ref={ref}
        className={cn('h-full rounded-pill bg-primary transition-[width]', className)}
        style={{ width: `${percent}%`, ...style }}
        {...props}
      />
    );
  },
);
MeterIndicator.displayName = 'Meter.Indicator';

export const Meter = {
  Root: MeterRoot,
  Track: MeterTrack,
  Indicator: MeterIndicator,
};
