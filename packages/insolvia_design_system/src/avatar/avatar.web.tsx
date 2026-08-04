// WEB LEAF — plain React DOM + Tailwind. Root is a fixed-size, clipped box;
// Image and Fallback both fill it absolutely and stack in DOM order, so
// Fallback (rendered after Image whenever it's showing) paints over a
// still-loading or broken `<img>` rather than the browser's broken-image icon
// — no conditional unmount of Image is needed for that, which keeps its
// onLoad/onError listeners attached for the image's whole lifetime.
import * as React from 'react';

import { cn } from '../lib/cn';
import {
  AvatarRootContext,
  avatarSizePx,
  useAvatarImageStatus,
  useAvatarRootContext,
  type AvatarRootOwnProps,
} from './avatar.props';

export interface AvatarRootProps
  extends React.ComponentPropsWithoutRef<'span'>,
    AvatarRootOwnProps {}

const AvatarRoot = React.forwardRef<HTMLSpanElement, AvatarRootProps>(
  ({ size = 'md', className, style, children, ...props }, ref) => {
    const [imageStatus, setImageStatus] = useAvatarImageStatus();
    const px = avatarSizePx[size];

    return (
      <span
        ref={ref}
        className={cn('relative inline-flex shrink-0 overflow-hidden rounded-pill', className)}
        style={{ width: px, height: px, ...style }}
        {...props}
      >
        <AvatarRootContext.Provider value={{ imageStatus, setImageStatus }}>
          {children}
        </AvatarRootContext.Provider>
      </span>
    );
  },
);
AvatarRoot.displayName = 'Avatar.Root';

export interface AvatarImageProps extends React.ComponentPropsWithoutRef<'img'> {
  alt: string;
}

const AvatarImage = React.forwardRef<HTMLImageElement, AvatarImageProps>(
  ({ className, onLoad, onError, ...props }, ref) => {
    const { setImageStatus } = useAvatarRootContext('Image');

    return (
      <img
        ref={ref}
        className={cn('absolute inset-0 h-full w-full object-cover', className)}
        onLoad={(event) => {
          onLoad?.(event);
          setImageStatus('loaded');
        }}
        onError={(event) => {
          onError?.(event);
          setImageStatus('error');
        }}
        {...props}
      />
    );
  },
);
AvatarImage.displayName = 'Avatar.Image';

export type AvatarFallbackProps = React.ComponentPropsWithoutRef<'span'>;

const AvatarFallback = React.forwardRef<HTMLSpanElement, AvatarFallbackProps>(
  ({ className, ...props }, ref) => {
    const { imageStatus } = useAvatarRootContext('Fallback');
    if (imageStatus === 'loaded') return null;

    return (
      <span
        ref={ref}
        className={cn(
          'absolute inset-0 flex items-center justify-center rounded-pill bg-surface-alt font-body text-xs font-medium text-muted',
          className,
        )}
        {...props}
      />
    );
  },
);
AvatarFallback.displayName = 'Avatar.Fallback';

export const Avatar = {
  Root: AvatarRoot,
  Image: AvatarImage,
  Fallback: AvatarFallback,
};
