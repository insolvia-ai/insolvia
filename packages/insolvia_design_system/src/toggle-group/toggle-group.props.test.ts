// Direct unit tests for the shared selection state machine, separate from the
// DOM tests: this module is what BOTH leaves execute, so its controlled/
// uncontrolled, single- and multi-select edge cases are pinned here once.
import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { useToggleGroupState } from './toggle-group.props';

describe('useToggleGroupState', () => {
  it('starts with nothing pressed without a defaultValue', () => {
    const { result } = renderHook(() =>
      useToggleGroupState(undefined, undefined, undefined, false, false),
    );

    expect(result.current.value).toEqual([]);
  });

  it('honours defaultValue on first render (uncontrolled)', () => {
    const { result } = renderHook(() =>
      useToggleGroupState(undefined, ['bold'], undefined, false, false),
    );

    expect(result.current.value).toEqual(['bold']);
  });

  describe('single-select (multiple = false)', () => {
    it('pressing one value replaces any other pressed value', () => {
      const { result } = renderHook(() =>
        useToggleGroupState(undefined, ['bold'], undefined, false, false),
      );

      act(() => result.current.toggle('italic'));

      expect(result.current.value).toEqual(['italic']);
    });

    it('pressing the already-pressed value clears the selection', () => {
      const { result } = renderHook(() =>
        useToggleGroupState(undefined, ['bold'], undefined, false, false),
      );

      act(() => result.current.toggle('bold'));

      expect(result.current.value).toEqual([]);
    });
  });

  describe('multi-select (multiple = true)', () => {
    it('keeps multiple values pressed at once', () => {
      const { result } = renderHook(() =>
        useToggleGroupState(undefined, undefined, undefined, true, false),
      );

      act(() => result.current.toggle('bold'));
      act(() => result.current.toggle('italic'));

      expect(result.current.value).toEqual(['bold', 'italic']);
    });

    it('unpresses only the toggled value', () => {
      const { result } = renderHook(() =>
        useToggleGroupState(undefined, ['bold', 'italic'], undefined, true, false),
      );

      act(() => result.current.toggle('bold'));

      expect(result.current.value).toEqual(['italic']);
    });
  });

  describe('controlled', () => {
    it('the value prop wins over internal state; onValueChange still fires', () => {
      const onValueChange = vi.fn();
      const { result, rerender } = renderHook(
        ({ value }: { value: string[] }) =>
          useToggleGroupState(value, undefined, onValueChange, false, false),
        { initialProps: { value: ['bold'] } },
      );

      act(() => result.current.toggle('italic'));

      // Controlled: still reports "bold" pressed until the parent supplies a
      // new `value`...
      expect(result.current.value).toEqual(['bold']);
      // ...but the parent is told what the group wants.
      expect(onValueChange).toHaveBeenCalledWith(['italic']);

      rerender({ value: ['italic'] });
      expect(result.current.value).toEqual(['italic']);
    });
  });

  it('carries the disabled flag through to context consumers', () => {
    const { result } = renderHook(() =>
      useToggleGroupState(undefined, undefined, undefined, false, true),
    );

    expect(result.current.disabled).toBe(true);
  });
});
