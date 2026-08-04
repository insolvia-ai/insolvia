// SHARED — `react` only (context). No react-dom, no react-native.
// Meter is Progress's static-gauge sibling — same Root/Track/Indicator shape
// and the same percent-of-range arithmetic — but it is deliberately its OWN
// small module rather than importing progress.props: a Meter always has a
// definite value (no indeterminate state) and a `min`, neither of which
// Progress models, and the litmus test in the conventions brief is explicit
// that two components this small duplicating a few lines of pure math beats
// coupling their state shapes together for a "shared" fraction that would
// immediately grow an indeterminate branch Meter has no use for.
import * as React from 'react';

export interface MeterRootContextValue {
  value: number;
  min: number;
  max: number;
  /** Pre-computed fill percentage, clamped to [0, 100]. */
  percent: number;
}

export const MeterRootContext = React.createContext<MeterRootContextValue | null>(null);

export function useMeterRootContext(part: string): MeterRootContextValue {
  const ctx = React.useContext(MeterRootContext);
  if (!ctx) throw new Error(`Meter.${part} must be rendered inside <Meter.Root>`);
  return ctx;
}

export interface MeterRootOwnProps {
  /** Required — a meter always displays a known value, never indeterminate. */
  value: number;
  /** Defaults to 0. */
  min?: number;
  /** Defaults to 100. */
  max?: number;
}

/**
 * The fill percentage for a value within [min, max], clamped to [0, 100]. A
 * non-positive range (`max <= min`) reports 0 rather than dividing by zero.
 */
export function meterPercent(value: number, min: number, max: number): number {
  const range = max - min;
  if (range <= 0) return 0;
  return Math.min(100, Math.max(0, ((value - min) / range) * 100));
}
