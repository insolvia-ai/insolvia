// NATIVE LEAF — React Native primitives, faithful radio group. Shares the
// exact selection state with the web leaf (radio-group.props); what is
// reimplemented here is the a11y surface: RN has no <button role="radio">, so
// each item is a Pressable with accessibilityRole="radio". Checked/disabled
// are reported BOTH ways: `accessibilityState` for real native platforms, and
// the `aria-checked`/`aria-disabled` props (real, typed RN props — no cast
// needed, see AccessibilityProps) for react-native-web, which is what
// actually ships (ADR 0004 — web is the only target). This split matters:
// react-native-web's DOM prop translation in the installed version reads
// `aria-checked`/`aria-disabled` (or their `accessibility*` aliases) but does
// NOT flatten the `accessibilityState` object into DOM attributes, so relying
// on `accessibilityState` alone would leave the shipped web app with no
// `aria-checked`/`aria-disabled` at all — the exact class of regression
// field.native.tsx's own aria cast comment warns about. There is no roving
// tabIndex/arrow-key nav here — that is a keyboard/focus concept the web leaf
// owns; touch selection is tap-to-select, same as every other RN control in
// this package.
import * as React from 'react';
import { Pressable, StyleSheet, View, type PressableProps, type ViewProps } from 'react-native';

import { radii, spacing } from '@insolvia-ai/tokens';

import { useNativeColors } from '../lib/native-theme';
import {
  RadioGroupItemContext,
  RadioGroupRootContext,
  useRadioGroupItemContext,
  useRadioGroupItemState,
  useRadioGroupRootContext,
  useRadioGroupState,
  type RadioGroupRootOwnProps,
} from './radio-group.props';

export interface RadioGroupRootProps extends ViewProps, RadioGroupRootOwnProps {}

const RadioGroupRoot = ({
  value,
  defaultValue,
  onValueChange,
  disabled = false,
  children,
  style,
  ...props
}: RadioGroupRootProps) => {
  const ctx = useRadioGroupState(value, defaultValue, onValueChange, disabled);
  return (
    <RadioGroupRootContext.Provider value={ctx}>
      <View
        accessibilityRole="radiogroup"
        aria-disabled={disabled ? true : undefined}
        style={[styles.root, style]}
        {...props}
      >
        {children}
      </View>
    </RadioGroupRootContext.Provider>
  );
};

export interface RadioGroupItemProps extends Omit<PressableProps, 'style'> {
  /** This item's identity — the value the group takes on when it is picked. */
  value: string;
  style?: PressableProps['style'];
}

const RadioGroupItem = ({
  value,
  disabled: itemDisabled = false,
  style,
  children,
  ...props
}: RadioGroupItemProps) => {
  const root = useRadioGroupRootContext('Item');
  const itemState = useRadioGroupItemState(value, root.value, root.disabled, itemDisabled ?? false);
  const c = useNativeColors();

  return (
    <RadioGroupItemContext.Provider value={itemState}>
      <Pressable
        accessibilityRole="radio"
        accessibilityState={{ checked: itemState.checked, disabled: itemState.disabled }}
        aria-checked={itemState.checked}
        disabled={itemState.disabled}
        onPress={() => {
          if (!itemState.disabled) root.setValue(value);
        }}
        style={(state) => [
          styles.item,
          {
            borderColor: itemState.checked ? c.primary : c.line,
            backgroundColor: c.card,
            opacity: itemState.disabled ? 0.5 : state.pressed ? 0.9 : 1,
          },
          typeof style === 'function' ? style(state) : style,
        ]}
        {...props}
      >
        {children}
      </Pressable>
    </RadioGroupItemContext.Provider>
  );
};

const RadioGroupIndicator = ({ children }: { children?: React.ReactNode }) => {
  const { checked } = useRadioGroupItemContext('Indicator');
  const c = useNativeColors();
  if (!checked) return null;
  return <View style={[styles.indicator, { backgroundColor: c.primary }]}>{children}</View>;
};

export const RadioGroup = {
  Root: RadioGroupRoot,
  Item: RadioGroupItem,
  Indicator: RadioGroupIndicator,
};

const styles = StyleSheet.create({
  root: { flexDirection: 'column', gap: spacing.sm },
  item: {
    height: 20,
    width: 20,
    borderRadius: radii.pill,
    borderWidth: 1,
    alignItems: 'center',
    justifyContent: 'center',
  },
  indicator: { height: 10, width: 10, borderRadius: radii.pill },
});
