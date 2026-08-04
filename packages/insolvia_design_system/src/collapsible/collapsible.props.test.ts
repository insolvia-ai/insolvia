// Direct unit tests for the shared state machine, separate from the DOM
// tests: this module is the part BOTH leaves execute, so its controlled vs.
// uncontrolled behavior is pinned here once instead of once per platform.
// Same pattern as accordion.props.test.ts.
import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { useCollapsibleState } from './collapsible.props';

describe('useCollapsibleState', () => {
  it('starts closed without defaultOpen', () => {
    const { result } = renderHook(() => useCollapsibleState(undefined, false, undefined, false));

    expect(result.current.open).toBe(false);
  });

  it('honours defaultOpen on first render', () => {
    const { result } = renderHook(() => useCollapsibleState(undefined, true, undefined, false));

    expect(result.current.open).toBe(true);
  });

  it('uncontrolled: toggle flips its own state and fires onOpenChange', () => {
    const onOpenChange = vi.fn();
    const { result } = renderHook(() =>
      useCollapsibleState(undefined, false, onOpenChange, false),
    );

    act(() => result.current.toggle());
    expect(result.current.open).toBe(true);
    expect(onOpenChange).toHaveBeenCalledWith(true);

    act(() => result.current.toggle());
    expect(result.current.open).toBe(false);
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it('disabled: toggle is a no-op', () => {
    const { result } = renderHook(() => useCollapsibleState(undefined, false, undefined, true));

    act(() => result.current.toggle());

    expect(result.current.open).toBe(false);
  });

  it('controlled: `open` always reflects the argument, not internal state, and toggle only requests a change', () => {
    const onOpenChange = vi.fn();
    const { result, rerender } = renderHook(
      ({ open }: { open: boolean }) => useCollapsibleState(open, false, onOpenChange, false),
      { initialProps: { open: false } },
    );

    act(() => result.current.toggle());

    // The hook asks for `true` via onOpenChange...
    expect(onOpenChange).toHaveBeenCalledWith(true);
    // ...but `open` does not move until the parent re-renders with a new value.
    expect(result.current.open).toBe(false);

    rerender({ open: true });

    expect(result.current.open).toBe(true);
  });

  it('derives a stable trigger/panel id pair from the same base id', () => {
    const { result } = renderHook(() => useCollapsibleState(undefined, false, undefined, false));

    expect(result.current.triggerId).toMatch(/-trigger$/);
    expect(result.current.panelId).toMatch(/-panel$/);
    expect(result.current.triggerId.replace(/-trigger$/, '')).toBe(
      result.current.panelId.replace(/-panel$/, ''),
    );
  });
});
