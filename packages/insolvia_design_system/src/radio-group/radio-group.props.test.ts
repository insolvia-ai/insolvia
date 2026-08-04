// Direct unit tests for the shared state machine, separate from the DOM
// tests: this module is the part BOTH leaves execute, so its controlled/
// uncontrolled edge cases and the tab-index arithmetic are pinned here once
// instead of once per platform.
import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { radioItemTabIndex, useRadioGroupItemState, useRadioGroupState } from './radio-group.props';

describe('useRadioGroupState', () => {
  it('starts with nothing selected without a defaultValue', () => {
    const { result } = renderHook(() => useRadioGroupState(undefined, undefined, undefined, false));

    expect(result.current.value).toBeUndefined();
  });

  it('honours defaultValue on first render', () => {
    const { result } = renderHook(() => useRadioGroupState(undefined, 'cash', undefined, false));

    expect(result.current.value).toBe('cash');
  });

  it('selecting a value updates uncontrolled state and fires onValueChange', () => {
    const onValueChange = vi.fn();
    const { result } = renderHook(() =>
      useRadioGroupState(undefined, undefined, onValueChange, false),
    );

    act(() => result.current.setValue('cash'));

    expect(result.current.value).toBe('cash');
    expect(onValueChange).toHaveBeenCalledWith('cash');
  });

  it('defers to the controlled value and only reports the requested change', () => {
    const onValueChange = vi.fn();
    const { result, rerender } = renderHook(
      ({ value }: { value: string | undefined }) =>
        useRadioGroupState(value, undefined, onValueChange, false),
      { initialProps: { value: 'cash' as string | undefined } },
    );

    act(() => result.current.setValue('card'));

    // Parent owns the value: nothing changes until it re-renders with a new one.
    expect(result.current.value).toBe('cash');
    expect(onValueChange).toHaveBeenCalledWith('card');

    rerender({ value: 'card' });
    expect(result.current.value).toBe('card');
  });
});

describe('useRadioGroupItemState', () => {
  it('is checked only when its value matches the group value', () => {
    expect(useRadioGroupItemState('cash', 'cash', false, false).checked).toBe(true);
    expect(useRadioGroupItemState('card', 'cash', false, false).checked).toBe(false);
  });

  it('is disabled when the group is disabled or the item disables itself', () => {
    expect(useRadioGroupItemState('cash', undefined, true, false).disabled).toBe(true);
    expect(useRadioGroupItemState('cash', undefined, false, true).disabled).toBe(true);
    expect(useRadioGroupItemState('cash', undefined, false, false).disabled).toBe(false);
  });
});

describe('radioItemTabIndex', () => {
  it('makes the checked item tabbable', () => {
    expect(radioItemTabIndex(true, true, false)).toBe(0);
  });

  it('makes the first item tabbable when nothing is selected', () => {
    expect(radioItemTabIndex(false, false, true)).toBe(0);
  });

  it('excludes every other unchecked item from the tab sequence', () => {
    expect(radioItemTabIndex(false, false, false)).toBe(-1);
    expect(radioItemTabIndex(false, true, true)).toBe(-1);
  });
});
