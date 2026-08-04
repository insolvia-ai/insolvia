// Direct unit tests for the shared image-status state machine and the size
// map both leaves render off of.
import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { avatarSizePx, useAvatarImageStatus } from './avatar.props';

describe('useAvatarImageStatus', () => {
  it('starts idle — Fallback shows until an image resolves', () => {
    const { result } = renderHook(() => useAvatarImageStatus());

    expect(result.current[0]).toBe('idle');
  });

  it('moves to loaded when set, and back to error when set again', () => {
    const { result } = renderHook(() => useAvatarImageStatus());

    act(() => result.current[1]('loaded'));
    expect(result.current[0]).toBe('loaded');

    act(() => result.current[1]('error'));
    expect(result.current[0]).toBe('error');
  });
});

describe('avatarSizePx', () => {
  it('maps each size to its fixed pixel box', () => {
    expect(avatarSizePx.sm).toBe(24);
    expect(avatarSizePx.md).toBe(32);
    expect(avatarSizePx.lg).toBe(40);
  });
});
