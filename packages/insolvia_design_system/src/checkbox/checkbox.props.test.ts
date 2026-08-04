// Direct unit tests for the shared state hook, separate from the DOM tests:
// this module is what BOTH leaves execute, so its controlled/uncontrolled and
// group-derived branches are pinned here once instead of once per platform.
import * as React from 'react';

import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import {
  CheckboxGroupContext,
  type CheckboxGroupContextValue,
} from '../checkbox-group/checkbox-group.props';
import { useCheckboxState } from './checkbox.props';

describe('useCheckboxState (standalone)', () => {
  it('starts unchecked without defaultChecked', () => {
    const { result } = renderHook(() =>
      useCheckboxState(undefined, undefined, undefined, false, false, undefined),
    );

    expect(result.current.checked).toBe(false);
  });

  it('honours defaultChecked on first render (uncontrolled)', () => {
    const { result } = renderHook(() =>
      useCheckboxState(undefined, true, undefined, false, false, undefined),
    );

    expect(result.current.checked).toBe(true);
  });

  it('toggle flips uncontrolled checked state and fires onCheckedChange', () => {
    const onCheckedChange = vi.fn();
    const { result } = renderHook(() =>
      useCheckboxState(undefined, false, onCheckedChange, false, false, undefined),
    );

    act(() => result.current.toggle());

    expect(result.current.checked).toBe(true);
    expect(onCheckedChange).toHaveBeenCalledWith(true);
  });

  it('in controlled mode, toggle reports the requested value but does not apply it locally', () => {
    const onCheckedChange = vi.fn();
    const { result, rerender } = renderHook(
      ({ checked }) => useCheckboxState(checked, false, onCheckedChange, false, false, undefined),
      { initialProps: { checked: true } },
    );

    act(() => result.current.toggle());

    expect(onCheckedChange).toHaveBeenCalledWith(false);
    // Parent owns the value: nothing changes until it re-renders with a new one.
    expect(result.current.checked).toBe(true);

    rerender({ checked: false });
    expect(result.current.checked).toBe(false);
  });

  it('a disabled checkbox ignores toggle entirely', () => {
    const onCheckedChange = vi.fn();
    const { result } = renderHook(() =>
      useCheckboxState(undefined, false, onCheckedChange, false, true, undefined),
    );

    act(() => result.current.toggle());

    expect(result.current.checked).toBe(false);
    expect(onCheckedChange).not.toHaveBeenCalled();
  });

  it('passes indeterminate through untouched', () => {
    const { result } = renderHook(() =>
      useCheckboxState(undefined, false, undefined, true, false, undefined),
    );

    expect(result.current.indeterminate).toBe(true);
    expect(result.current.checked).toBe(false);
  });
});

describe('useCheckboxState (inside a group)', () => {
  function wrapper(group: CheckboxGroupContextValue) {
    return function Wrapper({ children }: { children?: React.ReactNode }) {
      return React.createElement(CheckboxGroupContext.Provider, { value: group }, children);
    };
  }

  it('derives checked from group membership, ignoring standalone checked/defaultChecked', () => {
    const group: CheckboxGroupContextValue = {
      value: ['debts'],
      toggle: vi.fn(),
      disabled: false,
    };

    const { result } = renderHook(
      () => useCheckboxState(false, false, undefined, false, false, 'debts'),
      { wrapper: wrapper(group) },
    );

    expect(result.current.checked).toBe(true);
  });

  it('toggle calls the group toggle with this checkbox value, not local state', () => {
    const group: CheckboxGroupContextValue = {
      value: [],
      toggle: vi.fn(),
      disabled: false,
    };

    const { result } = renderHook(
      () => useCheckboxState(undefined, undefined, undefined, false, false, 'income'),
      { wrapper: wrapper(group) },
    );

    act(() => result.current.toggle());

    expect(group.toggle).toHaveBeenCalledWith('income');
  });

  it('is disabled when the group is disabled, even if this checkbox is not', () => {
    const group: CheckboxGroupContextValue = { value: [], toggle: vi.fn(), disabled: true };

    const { result } = renderHook(
      () => useCheckboxState(undefined, undefined, undefined, false, false, 'income'),
      { wrapper: wrapper(group) },
    );

    expect(result.current.disabled).toBe(true);

    act(() => result.current.toggle());
    expect(group.toggle).not.toHaveBeenCalled();
  });

  it('a group-membered checkbox WITHOUT a value prop stays standalone', () => {
    const group: CheckboxGroupContextValue = { value: ['x'], toggle: vi.fn(), disabled: false };

    const { result } = renderHook(
      () => useCheckboxState(undefined, true, undefined, false, false, undefined),
      { wrapper: wrapper(group) },
    );

    // defaultChecked wins — the group never gets consulted without a `value`.
    expect(result.current.checked).toBe(true);

    act(() => result.current.toggle());
    expect(group.toggle).not.toHaveBeenCalled();
  });
});
