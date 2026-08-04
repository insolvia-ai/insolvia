// NATIVE LEAF — React Native primitives. A single part, Root: bare RN has no
// `role="group"` DOM equivalent (see accordion.native.tsx's note on elements
// that simply don't map on native), so this is a plain View that provides
// ToggleGroupContext to any `Toggle` rendered inside it; the a11y surface
// lives entirely on each Toggle (`accessibilityRole` + a forwarded
// `aria-pressed` — see toggle.native.tsx's header for why), not here.
import * as React from 'react';
import { StyleSheet, View, type ViewProps } from 'react-native';

import { spacing } from '@insolvia-ai/tokens';

import {
  ToggleGroupContext,
  useToggleGroupState,
  type ToggleGroupRootOwnProps,
} from './toggle-group.props';

export interface ToggleGroupRootProps extends ViewProps, ToggleGroupRootOwnProps {}

const ToggleGroupRoot = ({
  value,
  defaultValue,
  onValueChange,
  multiple = false,
  disabled = false,
  children,
  style,
  ...props
}: ToggleGroupRootProps) => {
  const ctx = useToggleGroupState(value, defaultValue, onValueChange, multiple, disabled);
  return (
    <ToggleGroupContext.Provider value={ctx}>
      <View style={[styles.root, style]} {...props}>
        {children}
      </View>
    </ToggleGroupContext.Provider>
  );
};

export const ToggleGroup = { Root: ToggleGroupRoot };

const styles = StyleSheet.create({
  root: { flexDirection: 'row', gap: spacing.xs },
});
