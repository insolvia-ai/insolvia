// SHARED — `react` only (context + state). No react-dom, no react-native.
// Avatar's entire behavior model is the image-load status: `Image` reports
// its own load/error into it, `Fallback` renders whenever the status isn't
// `'loaded'`. Both leaves consume it verbatim; they differ only in the
// elements they render (`<img>`/`<span>` vs. `Image`/`View`+`Text`).
import * as React from 'react';

export type AvatarSize = 'sm' | 'md' | 'lg';

/** Fixed pixel box per size — shared so both leaves render identical boxes. */
export const avatarSizePx: Record<AvatarSize, number> = {
  sm: 24,
  md: 32,
  lg: 40,
};

export type AvatarImageStatus = 'idle' | 'loaded' | 'error';

export interface AvatarRootContextValue {
  imageStatus: AvatarImageStatus;
  setImageStatus: (status: AvatarImageStatus) => void;
}

export const AvatarRootContext = React.createContext<AvatarRootContextValue | null>(null);

export function useAvatarRootContext(part: string): AvatarRootContextValue {
  const ctx = React.useContext(AvatarRootContext);
  if (!ctx) throw new Error(`Avatar.${part} must be rendered inside <Avatar.Root>`);
  return ctx;
}

export interface AvatarRootOwnProps {
  /** Defaults to 'md' (32px). */
  size?: AvatarSize;
}

/**
 * The image-load state machine — pure React, identical on both platforms.
 * Starts `'idle'` (no image has resolved yet, so Fallback shows); `Image`
 * moves it to `'loaded'` or `'error'` from its own onLoad/onError.
 */
export function useAvatarImageStatus(): [AvatarImageStatus, (status: AvatarImageStatus) => void] {
  return React.useState<AvatarImageStatus>('idle');
}
