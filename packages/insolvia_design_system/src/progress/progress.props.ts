// SHARED — `react` only (context). No react-dom, no react-native. A progress
// bar carries no interaction or ids to derive, so this module is small: the
// context that lets Track/Indicator read the root's value without prop
// drilling, and the pure percent computation both leaves render off of.
import * as React from 'react';

export interface ProgressRootContextValue {
  /** `null` means indeterminate — the value both leaves branch on. */
  value: number | null;
  max: number;
  /** Pre-computed fill percentage, clamped to [0, 100]. 0 while indeterminate. */
  percent: number;
}

export const ProgressRootContext = React.createContext<ProgressRootContextValue | null>(null);

export function useProgressRootContext(part: string): ProgressRootContextValue {
  const ctx = React.useContext(ProgressRootContext);
  if (!ctx) throw new Error(`Progress.${part} must be rendered inside <Progress.Root>`);
  return ctx;
}

export interface ProgressRootOwnProps {
  /** `null` (the default) renders the indeterminate state. */
  value?: number | null;
  /** Defaults to 100. */
  max?: number;
}

/**
 * The fill percentage for a progress value against its max, clamped to
 * [0, 100]. `null` (indeterminate) and `undefined` both report 0 — a leaf
 * decides separately, from the raw `value`, whether to render the
 * indeterminate visual; this function only answers "how full is it."
 * A non-positive `max` also reports 0 rather than dividing by zero.
 */
export function progressPercent(value: number | null | undefined, max: number): number {
  if (value === null || value === undefined || max <= 0) return 0;
  return Math.min(100, Math.max(0, (value / max) * 100));
}
