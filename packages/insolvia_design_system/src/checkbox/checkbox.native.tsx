// NATIVE LEAF — React Native primitives, faithful checkbox. Shares the exact
// state model with the web leaf (checkbox.props); what is reimplemented here
// is the a11y surface: RN has no `<button role="checkbox">`, so behavior maps
// onto Pressable + accessibilityRole="checkbox" + accessibilityState (the real
// signal iOS/Android accessibility services read) — PLUS a directly-set
// `aria-checked` prop, which IS in react-native's own AccessibilityProps
// (unlike aria-describedby/aria-invalid on Field.Control) and is what
// react-native-web actually writes to the DOM: this version of
// react-native-web does not derive `aria-checked` from `accessibilityState`,
// so setting only the latter would leave app-web's DOM (and this leaf's own
// tests) with no observable checked state.
import * as React from 'react';
import {
  Pressable,
  StyleSheet,
  View,
  type PressableProps,
  type StyleProp,
  type ViewStyle,
} from 'react-native';

import { radii } from '@insolvia-ai/tokens';

import { useNativeColors } from '../lib/native-theme';
import {
  CheckboxRootContext,
  useCheckboxRootContext,
  useCheckboxState,
  type CheckboxIndicatorContextValue,
  type CheckboxRootOwnProps,
} from './checkbox.props';

export interface CheckboxRootProps
  extends Omit<PressableProps, 'disabled' | 'children' | 'style'>,
    CheckboxRootOwnProps {
  children?: React.ReactNode;
  /** Plain style only (no function-of-press-state form) — see Field/Accordion. */
  style?: StyleProp<ViewStyle>;
}

const CheckboxRoot = ({
  checked,
  defaultChecked,
  onCheckedChange,
  indeterminate = false,
  disabled = false,
  value,
  style,
  onPress,
  children,
  ...props
}: CheckboxRootProps) => {
  const state = useCheckboxState(
    checked,
    defaultChecked,
    onCheckedChange,
    indeterminate,
    disabled,
    value,
  );
  const c = useNativeColors();

  const ctx: CheckboxIndicatorContextValue = {
    checked: state.checked,
    indeterminate: state.indeterminate,
  };

  const active = state.checked || state.indeterminate;

  return (
    <CheckboxRootContext.Provider value={ctx}>
      <Pressable
        accessibilityRole="checkbox"
        accessibilityState={{
          checked: state.indeterminate ? 'mixed' : state.checked,
          disabled: state.disabled,
        }}
        aria-checked={state.indeterminate ? 'mixed' : state.checked}
        disabled={state.disabled}
        onPress={(event) => {
          onPress?.(event);
          state.toggle();
        }}
        style={[
          styles.base,
          {
            borderColor: active ? c.primary : c.line,
            backgroundColor: active ? c.primary : c.card,
            opacity: state.disabled ? 0.5 : 1,
          },
          style,
        ]}
        {...props}
      >
        {children}
      </Pressable>
    </CheckboxRootContext.Provider>
  );
};

const CheckboxIndicator = ({ children }: { children?: React.ReactNode }) => {
  const { checked, indeterminate } = useCheckboxRootContext('Indicator');
  if (!checked && !indeterminate) return null;
  return <View style={styles.indicator}>{children}</View>;
};

export const Checkbox = {
  Root: CheckboxRoot,
  Indicator: CheckboxIndicator,
};

const styles = StyleSheet.create({
  base: {
    height: 20,
    width: 20,
    borderRadius: radii.sm,
    borderWidth: 1,
    alignItems: 'center',
    justifyContent: 'center',
  },
  indicator: {
    alignItems: 'center',
    justifyContent: 'center',
  },
});
