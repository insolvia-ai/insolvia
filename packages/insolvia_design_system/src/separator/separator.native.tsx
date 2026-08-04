// NATIVE LEAF — React Native primitives. RN's `AccessibilityRole` enum has no
// `separator` value, so `accessibilityRole="none"` is the closest honest
// mapping: this divider is a purely visual hairline with no interactive or
// landmark semantics for a screen reader on native (unlike the web leaf,
// which gets a real ARIA `separator` role for free). `StyleSheet.hairlineWidth`
// gives the thinnest crisp line the device can render — a bare `1` can look
// thick on high-density screens. Color still resolves from useNativeColors()
// at render time, never baked into StyleSheet.create.
import * as React from 'react';
import { StyleSheet, View, type ViewProps } from 'react-native';

import { useNativeColors } from '../lib/native-theme';
import type { SeparatorOwnProps } from './separator.props';

export interface SeparatorProps extends ViewProps, SeparatorOwnProps {}

export function Separator({ orientation = 'horizontal', style, ...props }: SeparatorProps) {
  const c = useNativeColors();
  return (
    <View
      accessibilityRole="none"
      style={[
        orientation === 'horizontal' ? styles.horizontal : styles.vertical,
        { backgroundColor: c.line },
        style,
      ]}
      {...props}
    />
  );
}

const styles = StyleSheet.create({
  horizontal: { height: StyleSheet.hairlineWidth, width: '100%' },
  vertical: { width: StyleSheet.hairlineWidth, alignSelf: 'stretch' },
});
