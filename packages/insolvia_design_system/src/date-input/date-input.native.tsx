// NATIVE LEAF — a React Native TextInput over the same shared mask
// (date-input.props). This is the leaf the app renders on web, so it is the one
// that has to be right; it deliberately mirrors Field's own control styling,
// because a date field sitting next to a text field should not look like a
// different species.
//
// No DateTimePicker, on native or web. The dates this collects are historical
// (a debt incurred in 2016, a prior filing in 2011), and a wheel or calendar is
// the slowest way to reach those — see the note in date-input.props. It also
// keeps the component free of a native module, which matters for a package that
// declares no react-native dependency of its own.
import * as React from 'react';
import { StyleSheet, TextInput, View, type TextInputProps } from 'react-native';

import { radii, spacing } from '@insolvia-ai/tokens';

import { FieldContext } from '../field/field.props';
import { useNativeColors } from '../lib/native-theme';
import {
  DATE_FORMAT,
  isErrorStatus,
  useDateInputState,
  type DateInputOwnProps,
} from './date-input.props';

export interface DateInputProps
  extends
    Omit<TextInputProps, 'value' | 'defaultValue' | 'onChangeText' | 'editable'>,
    DateInputOwnProps {
  /** Names the control when it is not inside a `<Field.Root>`. */
  'aria-label'?: string | undefined;
}

export const DateInput = ({
  value,
  defaultValue,
  onValueChange,
  min,
  max,
  disabled = false,
  name: _name,
  placeholder = DATE_FORMAT,
  style,
  ...props
}: DateInputProps) => {
  const field = React.useContext(FieldContext);
  const c = useNativeColors();
  const { text, setText, status } = useDateInputState({
    value,
    defaultValue,
    onValueChange,
    min,
    max,
  });

  const invalid = isErrorStatus(status) || (field?.invalid ?? false);

  // `aria-describedby` and `aria-invalid` are web-only and outside RN's own
  // types; react-native-web forwards them to the DOM regardless. Omitted rather
  // than set to undefined, so the control never points at an element that does
  // not exist — the same shape Field's native leaf uses.
  const webAria = {
    ...(field?.describedBy === undefined ? {} : { 'aria-describedby': field.describedBy }),
    ...(invalid ? { 'aria-invalid': true } : {}),
  } as TextInputProps;

  return (
    <View style={styles.root}>
      <TextInput
        nativeID={field?.controlId}
        aria-labelledby={field?.labelId}
        aria-label={props['aria-label']}
        {...webAria}
        accessibilityState={{ disabled }}
        editable={!disabled}
        value={text}
        onChangeText={setText}
        placeholder={placeholder}
        placeholderTextColor={c.muted}
        // A digit keypad on a phone; harmless on web, where it is advisory.
        keyboardType="number-pad"
        style={[
          styles.control,
          {
            borderColor: invalid ? c.danger : c.line,
            backgroundColor: disabled ? c.surfaceAlt : c.card,
            color: disabled ? c.muted : c.ink,
          },
          style,
        ]}
        {...props}
      />
    </View>
  );
};

const styles = StyleSheet.create({
  root: { width: '100%' },
  control: {
    // 44dp, the WCAG 2.5.5 target-size floor the app enforces. Field's own
    // control is 40dp; a date input is reached by tap far more often than a
    // long-form text field, so it takes the taller of the two.
    height: 44,
    paddingHorizontal: spacing.sm,
    borderWidth: 1,
    borderRadius: radii.md,
    fontSize: 14,
  },
});
