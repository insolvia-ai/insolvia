// NATIVE LEAF — React Native primitives over @insolvia-ai/tokens. Shares the
// image-load status model with the web leaf (avatar.props); the elements
// reimplement onto Image + View/Text, both absolutely filling the Root box so
// Fallback stacks over a still-loading/broken Image the same way the web leaf
// does. Colors come from useNativeColors() at render time (scheme-aware);
// only scheme-independent layout sits in StyleSheet.create.
import * as React from 'react';
import { Image, StyleSheet, Text, View, type ImageProps, type ViewProps } from 'react-native';

import { radii } from '@insolvia-ai/tokens';

import { useNativeColors } from '../lib/native-theme';
import {
  AvatarRootContext,
  avatarSizePx,
  useAvatarImageStatus,
  useAvatarRootContext,
  type AvatarRootOwnProps,
} from './avatar.props';

export interface AvatarRootProps extends Omit<ViewProps, 'children'>, AvatarRootOwnProps {
  children?: React.ReactNode;
}

const AvatarRoot = ({ size = 'md', children, style, ...props }: AvatarRootProps) => {
  const [imageStatus, setImageStatus] = useAvatarImageStatus();
  const px = avatarSizePx[size];

  return (
    <View
      style={[styles.root, { width: px, height: px, borderRadius: radii.pill }, style]}
      {...props}
    >
      <AvatarRootContext.Provider value={{ imageStatus, setImageStatus }}>
        {children}
      </AvatarRootContext.Provider>
    </View>
  );
};

// `alt` is optional on RN's own ImageProps; it's required here (same as the
// web leaf) because it's the only accessible name this element can carry.
export interface AvatarImageProps extends Omit<ImageProps, 'alt'> {
  alt: string;
}

const AvatarImage = ({ style, onLoad, onError, ...props }: AvatarImageProps) => {
  const { setImageStatus } = useAvatarRootContext('Image');

  return (
    <Image
      style={[styles.image, style]}
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
};

export interface AvatarFallbackProps {
  children?: React.ReactNode;
  style?: ViewProps['style'];
}

const AvatarFallback = ({ children, style }: AvatarFallbackProps) => {
  const { imageStatus } = useAvatarRootContext('Fallback');
  const c = useNativeColors();
  if (imageStatus === 'loaded') return null;

  return (
    <View
      style={[styles.fallback, { backgroundColor: c.surfaceAlt, borderRadius: radii.pill }, style]}
    >
      <Text style={[styles.fallbackText, { color: c.muted }]}>{children}</Text>
    </View>
  );
};

export const Avatar = {
  Root: AvatarRoot,
  Image: AvatarImage,
  Fallback: AvatarFallback,
};

const styles = StyleSheet.create({
  root: { overflow: 'hidden' },
  image: { position: 'absolute', top: 0, left: 0, right: 0, bottom: 0, width: '100%', height: '100%' },
  fallback: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    bottom: 0,
    alignItems: 'center',
    justifyContent: 'center',
  },
  fallbackText: { fontSize: 12, fontWeight: '500' },
});
