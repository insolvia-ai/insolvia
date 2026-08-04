// NATIVE LEAF — React Native primitives. Shares the exact state model with the
// web leaf (checkbox-group.props); what is reimplemented here is the a11y
// surface. RN's `AccessibilityRole` union has no "group" entry, but the newer,
// web-ARIA-aligned `role` prop does (`Role` includes `'group'`) and
// react-native-web forwards it to the DOM's `role` attribute verbatim — the
// exact `role="group"` the web leaf sets directly, so this is the real native
// equivalent rather than an approximation.
import * as React from 'react';
import { StyleSheet, View, type ViewProps } from 'react-native';

import { spacing } from '@insolvia-ai/tokens';

import {
  CheckboxGroupContext,
  useCheckboxGroupState,
  type CheckboxGroupRootOwnProps,
} from './checkbox-group.props';

export interface CheckboxGroupRootProps extends ViewProps, CheckboxGroupRootOwnProps {}

const CheckboxGroupRoot = ({
  value,
  defaultValue,
  onValueChange,
  disabled = false,
  children,
  style,
  ...props
}: CheckboxGroupRootProps) => {
  const ctx = useCheckboxGroupState(value, defaultValue, onValueChange, disabled);
  return (
    <CheckboxGroupContext.Provider value={ctx}>
      <View role="group" style={[styles.root, style]} {...props}>
        {children}
      </View>
    </CheckboxGroupContext.Provider>
  );
};

export const CheckboxGroup = {
  Root: CheckboxGroupRoot,
};

const styles = StyleSheet.create({
  root: { flexDirection: 'column', gap: spacing.sm },
});
