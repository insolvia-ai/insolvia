// Direct unit tests for the shared state machine, separate from the DOM tests:
// this module is the part BOTH leaves execute, so its edge cases (openMultiple,
// defaultValue) are pinned here once instead of once per platform.
import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { useAccordionState } from './accordion.props';

describe('useAccordionState', () => {
  it('starts fully closed without a defaultValue', () => {
    const { result } = renderHook(() => useAccordionState(undefined, true));

    expect(result.current.isOpen('cost')).toBe(false);
  });

  it('honours defaultValue on first render', () => {
    const { result } = renderHook(() => useAccordionState(['cost'], true));

    expect(result.current.isOpen('cost')).toBe(true);
    expect(result.current.isOpen('security')).toBe(false);
  });

  it('toggle opens a closed item and closes an open one', () => {
    const { result } = renderHook(() => useAccordionState(undefined, true));

    act(() => result.current.toggle('cost'));
    expect(result.current.isOpen('cost')).toBe(true);

    act(() => result.current.toggle('cost'));
    expect(result.current.isOpen('cost')).toBe(false);
  });

  it('keeps multiple items open when openMultiple is true', () => {
    const { result } = renderHook(() => useAccordionState(undefined, true));

    act(() => result.current.toggle('cost'));
    act(() => result.current.toggle('security'));

    expect(result.current.isOpen('cost')).toBe(true);
    expect(result.current.isOpen('security')).toBe(true);
  });

  it('closes the open item when another opens and openMultiple is false', () => {
    const { result } = renderHook(() => useAccordionState(['cost'], false));

    act(() => result.current.toggle('security'));

    expect(result.current.isOpen('cost')).toBe(false);
    expect(result.current.isOpen('security')).toBe(true);
  });
});
