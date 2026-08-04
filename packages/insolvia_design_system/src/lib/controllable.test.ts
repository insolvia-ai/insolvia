import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { useControllableState } from './controllable';

describe('useControllableState', () => {
  it('owns the state when no controlled value is given', () => {
    const onChange = vi.fn();
    const { result } = renderHook(() => useControllableState<boolean>(undefined, false, onChange));

    expect(result.current[0]).toBe(false);
    act(() => result.current[1](true));
    expect(result.current[0]).toBe(true);
    expect(onChange).toHaveBeenCalledWith(true);
  });

  it('defers to the controlled value and only reports the requested change', () => {
    const onChange = vi.fn();
    const { result, rerender } = renderHook(
      ({ value }) => useControllableState<string>(value, 'a', onChange),
      { initialProps: { value: 'b' } },
    );

    expect(result.current[0]).toBe('b');
    act(() => result.current[1]('c'));
    // Parent owns the value: nothing changes until it re-renders with a new one.
    expect(result.current[0]).toBe('b');
    expect(onChange).toHaveBeenCalledWith('c');
    rerender({ value: 'c' });
    expect(result.current[0]).toBe('c');
  });
});
