// The alert dialog deliberately reuses the dialog's state machine — these
// tests pin the controlled/uncontrolled contract THROUGH the alert-dialog
// props module, proving the re-export stays wired to the shared machine.
import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { useAlertDialogState } from './alert-dialog.props';

describe('useAlertDialogState', () => {
  it('uncontrolled: starts from defaultOpen and toggles internally', () => {
    const onOpenChange = vi.fn();
    const { result } = renderHook(() => useAlertDialogState(undefined, false, onOpenChange));

    expect(result.current.open).toBe(false);

    act(() => result.current.setOpen(true));
    expect(result.current.open).toBe(true);
    expect(onOpenChange).toHaveBeenCalledWith(true);
  });

  it('controlled: the open prop wins and setOpen only requests the change', () => {
    const onOpenChange = vi.fn();
    const { result, rerender } = renderHook(
      ({ open }: { open: boolean }) => useAlertDialogState(open, undefined, onOpenChange),
      { initialProps: { open: true } },
    );

    act(() => result.current.setOpen(false));

    // The parent has not applied the change, so the value must not move…
    expect(result.current.open).toBe(true);
    // …but the request must have been surfaced.
    expect(onOpenChange).toHaveBeenCalledWith(false);

    rerender({ open: false });
    expect(result.current.open).toBe(false);
  });
});
