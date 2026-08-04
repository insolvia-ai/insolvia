// Direct unit tests for the shared pressed-state model, separate from the DOM
// tests: this module is what BOTH leaves execute, so its controlled/
// uncontrolled and group-derived branches are pinned here once instead of
// once per platform. No JSX here (the file stays `.ts`, per convention) — the
// ToggleGroupContext provider wrapper is built with React.createElement.
import * as React from 'react';

import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import {
  ToggleGroupContext,
  type ToggleGroupContextValue,
} from '../toggle-group/toggle-group.props';
import { useToggleState } from './toggle.props';

describe('useToggleState (standalone)', () => {
  it('starts unpressed without defaultPressed', () => {
    const { result } = renderHook(() =>
      useToggleState(undefined, undefined, undefined, undefined, undefined),
    );

    expect(result.current.pressed).toBe(false);
  });

  it('honours defaultPressed on first render (uncontrolled)', () => {
    const { result } = renderHook(() =>
      useToggleState(undefined, true, undefined, undefined, undefined),
    );

    expect(result.current.pressed).toBe(true);
  });

  it('toggle flips uncontrolled pressed state and fires onPressedChange', () => {
    const onPressedChange = vi.fn();
    const { result } = renderHook(() =>
      useToggleState(undefined, false, onPressedChange, undefined, undefined),
    );

    act(() => result.current.toggle());

    expect(result.current.pressed).toBe(true);
    expect(onPressedChange).toHaveBeenCalledWith(true);

    act(() => result.current.toggle());

    expect(result.current.pressed).toBe(false);
    expect(onPressedChange).toHaveBeenCalledWith(false);
  });

  it('in controlled mode, toggle reports the requested value but does not apply it locally', () => {
    const onPressedChange = vi.fn();
    const { result, rerender } = renderHook(
      ({ pressed }: { pressed: boolean }) =>
        useToggleState(pressed, undefined, onPressedChange, undefined, undefined),
      { initialProps: { pressed: true } },
    );

    act(() => result.current.toggle());

    expect(onPressedChange).toHaveBeenCalledWith(false);
    // Parent owns the value: nothing changes until it re-renders with a new one.
    expect(result.current.pressed).toBe(true);

    rerender({ pressed: false });
    expect(result.current.pressed).toBe(false);
  });

  it('a disabled toggle ignores toggle() entirely', () => {
    const onPressedChange = vi.fn();
    const { result } = renderHook(() =>
      useToggleState(undefined, false, onPressedChange, true, undefined),
    );

    act(() => result.current.toggle());

    expect(result.current.pressed).toBe(false);
    expect(onPressedChange).not.toHaveBeenCalled();
  });
});

describe('useToggleState (inside a group)', () => {
  function wrapper(group: ToggleGroupContextValue) {
    return function Wrapper({ children }: { children?: React.ReactNode }) {
      return React.createElement(ToggleGroupContext.Provider, { value: group }, children);
    };
  }

  it('derives pressed from group membership, ignoring standalone pressed/defaultPressed', () => {
    const group: ToggleGroupContextValue = {
      value: ['bold'],
      toggle: vi.fn(),
      disabled: false,
    };

    const { result } = renderHook(
      () => useToggleState(false, false, undefined, undefined, 'bold'),
      { wrapper: wrapper(group) },
    );

    expect(result.current.pressed).toBe(true);
  });

  it('toggle calls the group toggle with this value, not local state', () => {
    const group: ToggleGroupContextValue = {
      value: [],
      toggle: vi.fn(),
      disabled: false,
    };

    const { result } = renderHook(
      () => useToggleState(undefined, undefined, undefined, undefined, 'italic'),
      { wrapper: wrapper(group) },
    );

    act(() => result.current.toggle());

    expect(group.toggle).toHaveBeenCalledWith('italic');
  });

  it('is disabled when the group is disabled, even if this toggle is not', () => {
    const group: ToggleGroupContextValue = { value: [], toggle: vi.fn(), disabled: true };

    const { result } = renderHook(
      () => useToggleState(undefined, undefined, undefined, undefined, 'italic'),
      { wrapper: wrapper(group) },
    );

    expect(result.current.disabled).toBe(true);

    act(() => result.current.toggle());
    expect(group.toggle).not.toHaveBeenCalled();
  });

  it('a group-membered toggle WITHOUT a value prop stays standalone', () => {
    const group: ToggleGroupContextValue = { value: ['x'], toggle: vi.fn(), disabled: false };

    const { result } = renderHook(
      () => useToggleState(undefined, true, undefined, undefined, undefined),
      { wrapper: wrapper(group) },
    );

    // defaultPressed wins — the group is never consulted without a `value`.
    expect(result.current.pressed).toBe(true);

    act(() => result.current.toggle());
    expect(group.toggle).not.toHaveBeenCalled();
  });
});
