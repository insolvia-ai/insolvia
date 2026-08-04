// Direct unit tests for the shared state machine, separate from the DOM
// tests: this module is the part BOTH leaves execute, so its controlled/
// uncontrolled and disabled edge cases are pinned here once instead of once
// per platform.
import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { useSwitchState } from './switch.props';

describe('useSwitchState', () => {
  it('starts off without defaultChecked', () => {
    const { result } = renderHook(() => useSwitchState(undefined, false, undefined, false));

    expect(result.current.checked).toBe(false);
  });

  it('honours defaultChecked on first render', () => {
    const { result } = renderHook(() => useSwitchState(undefined, true, undefined, false));

    expect(result.current.checked).toBe(true);
  });

  it('toggles uncontrolled state and fires onCheckedChange', () => {
    const onCheckedChange = vi.fn();
    const { result } = renderHook(() => useSwitchState(undefined, false, onCheckedChange, false));

    act(() => result.current.toggle());

    expect(result.current.checked).toBe(true);
    expect(onCheckedChange).toHaveBeenCalledWith(true);
  });

  it('defers to the controlled value and only reports the requested change', () => {
    const onCheckedChange = vi.fn();
    const { result, rerender } = renderHook(
      ({ checked }: { checked: boolean }) => useSwitchState(checked, false, onCheckedChange, false),
      { initialProps: { checked: false } },
    );

    act(() => result.current.toggle());

    // Parent owns the value: nothing changes until it re-renders with a new one.
    expect(result.current.checked).toBe(false);
    expect(onCheckedChange).toHaveBeenCalledWith(true);

    rerender({ checked: true });
    expect(result.current.checked).toBe(true);
  });

  it('never toggles, and never fires onCheckedChange, when disabled', () => {
    const onCheckedChange = vi.fn();
    const { result } = renderHook(() => useSwitchState(undefined, false, onCheckedChange, true));

    act(() => result.current.toggle());

    expect(result.current.checked).toBe(false);
    expect(onCheckedChange).not.toHaveBeenCalled();
  });
});
