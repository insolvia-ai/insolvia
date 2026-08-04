// Direct unit tests for the shared state machine, separate from the DOM
// tests: this module is the part BOTH leaves execute, so its controlled vs.
// uncontrolled logic is pinned here once instead of once per platform.
import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { getPanelId, getTabId, useTabsState } from './tabs.props';

describe('useTabsState', () => {
  it('starts on defaultValue when uncontrolled', () => {
    const { result } = renderHook(() => useTabsState(undefined, 'cost', undefined));

    expect(result.current.value).toBe('cost');
  });

  it('updates its own value on setValue when uncontrolled', () => {
    const { result } = renderHook(() => useTabsState(undefined, 'cost', undefined));

    act(() => result.current.setValue('security'));

    expect(result.current.value).toBe('security');
  });

  it('calls onValueChange when uncontrolled', () => {
    const onValueChange = vi.fn();
    const { result } = renderHook(() => useTabsState(undefined, 'cost', onValueChange));

    act(() => result.current.setValue('security'));

    expect(onValueChange).toHaveBeenCalledWith('security');
  });

  it('is driven by the `value` argument when controlled, ignoring defaultValue', () => {
    const { result } = renderHook(() => useTabsState('security', 'cost', undefined));

    expect(result.current.value).toBe('security');
  });

  it('does not change its own value when controlled — the caller must re-render with a new value', () => {
    const onValueChange = vi.fn();
    const { result, rerender } = renderHook(
      ({ value }) => useTabsState(value, 'cost', onValueChange),
      { initialProps: { value: 'security' } },
    );

    act(() => result.current.setValue('billing'));

    expect(onValueChange).toHaveBeenCalledWith('billing');
    expect(result.current.value).toBe('security');

    rerender({ value: 'billing' });
    expect(result.current.value).toBe('billing');
  });

  it('derives a stable, distinct rootId across renders', () => {
    const { result, rerender } = renderHook(() => useTabsState(undefined, 'cost', undefined));
    const firstId = result.current.rootId;

    rerender();

    expect(result.current.rootId).toBe(firstId);
    expect(firstId.length).toBeGreaterThan(0);
  });
});

describe('getTabId / getPanelId', () => {
  it('derives matching, distinct ids for the same rootId/value pair', () => {
    const tabId = getTabId('r1', 'cost');
    const panelId = getPanelId('r1', 'cost');

    expect(tabId).not.toBe(panelId);
    expect(tabId).toContain('cost');
    expect(panelId).toContain('cost');
  });

  it('is a pure function of its arguments', () => {
    expect(getTabId('r1', 'cost')).toBe(getTabId('r1', 'cost'));
    expect(getTabId('r1', 'cost')).not.toBe(getTabId('r2', 'cost'));
    expect(getTabId('r1', 'cost')).not.toBe(getTabId('r1', 'security'));
  });
});
