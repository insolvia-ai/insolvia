// Direct unit tests for the shared state machine, separate from the DOM tests:
// this module is the part BOTH leaves (and the alert dialog) execute, so the
// controlled/uncontrolled split and the id derivation are pinned here once.
import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { useDialogState } from './dialog.props';

describe('useDialogState', () => {
  it('uncontrolled: starts closed without defaultOpen and toggles internally', () => {
    const onOpenChange = vi.fn();
    const { result } = renderHook(() => useDialogState(undefined, undefined, onOpenChange));

    expect(result.current.open).toBe(false);

    act(() => result.current.setOpen(true));
    expect(result.current.open).toBe(true);
    expect(onOpenChange).toHaveBeenCalledWith(true);

    act(() => result.current.setOpen(false));
    expect(result.current.open).toBe(false);
    expect(onOpenChange).toHaveBeenLastCalledWith(false);
  });

  it('uncontrolled: honours defaultOpen on first render', () => {
    const { result } = renderHook(() => useDialogState(undefined, true, undefined));

    expect(result.current.open).toBe(true);
  });

  it('controlled: the open prop wins and setOpen only requests the change', () => {
    const onOpenChange = vi.fn();
    const { result, rerender } = renderHook(
      ({ open }: { open: boolean }) => useDialogState(open, undefined, onOpenChange),
      { initialProps: { open: false } },
    );

    act(() => result.current.setOpen(true));

    // The parent has not applied the change, so the value must not move…
    expect(result.current.open).toBe(false);
    // …but the request must have been surfaced.
    expect(onOpenChange).toHaveBeenCalledWith(true);

    rerender({ open: true });
    expect(result.current.open).toBe(true);
  });

  it('derives distinct, suffixed title and description ids', () => {
    const { result } = renderHook(() => useDialogState(undefined, undefined, undefined));

    expect(result.current.titleId).toMatch(/-title$/);
    expect(result.current.descriptionId).toMatch(/-description$/);
    expect(result.current.titleId).not.toBe(result.current.descriptionId);
  });
});
