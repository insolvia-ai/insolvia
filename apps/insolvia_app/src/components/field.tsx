import { useId } from 'react';
import { StyleSheet, Text, TextInput, View } from 'react-native';
import type { StyleProp, TextInputProps, ViewStyle } from 'react-native';

import { fontSizes, spacing, useTheme } from '@/theme';

export interface FieldProps extends Omit<
  TextInputProps,
  'aria-labelledby' | 'aria-describedby' | 'nativeID' | 'id' | 'style'
> {
  /** The visible label. Required — this is the input's accessible name. */
  label: string;

  /** Optional helper text, wired up with `aria-describedby`. */
  hint?: string;

  /** Overrides the generated id prefix. Only needed to make ids predictable. */
  idPrefix?: string;

  /** Style for the wrapper, not the input. */
  style?: StyleProp<ViewStyle>;
}

/**
 * A labelled text input — **the only way to render an input in this app.**
 *
 * No screen may use a bare `TextInput`: an unlabelled input is unusable with a
 * screen reader, and the component library this codebase rejected shipped
 * exactly that defect (its label rendered a `<div>` with nothing tying it to the
 * input, which then carried a generic `aria-label="Input Field"`). Routing every
 * input through here means the mistake cannot be written by accident, and
 * `field.test.tsx` asserts the wiring.
 *
 * The wiring is `nativeID` on the label + `aria-labelledby` on the input, which
 * react-native-web emits as a correctly associated pair — verified in the spike
 * with bare primitives, so no library is needed for it.
 */
export function Field({ label, hint, idPrefix, style, ...input }: FieldProps) {
  const theme = useTheme();
  const generated = useId();
  const prefix = idPrefix ?? generated;
  const labelId = `${prefix}-label`;
  const hintId = `${prefix}-hint`;

  // `aria-labelledby` is in react-native's types; `aria-describedby` is not —
  // it is web-only, and react-native-web forwards it to the DOM regardless. Same
  // reasoning as the assertion in heading.tsx: contained, and documented where
  // the semantics live. The prop is OMITTED, not set to undefined, when there is
  // no hint, so the input never points at an element that does not exist.
  const describedBy = (hint === undefined ? {} : { 'aria-describedby': hintId }) as TextInputProps;

  return (
    <View style={[styles.wrapper, style]}>
      <Text
        nativeID={labelId}
        style={[styles.label, { color: theme.colors.ink, fontFamily: theme.typography.body }]}
      >
        {label}
      </Text>
      <TextInput
        aria-labelledby={labelId}
        {...describedBy}
        placeholderTextColor={theme.colors.muted}
        style={[
          styles.input,
          {
            backgroundColor: theme.colors.card,
            borderColor: theme.colors.line,
            borderRadius: theme.radii.sm,
            color: theme.colors.ink,
            fontFamily: theme.typography.body,
          },
        ]}
        {...input}
      />
      {hint === undefined ? null : (
        <Text
          nativeID={hintId}
          style={[styles.hint, { color: theme.colors.muted, fontFamily: theme.typography.body }]}
        >
          {hint}
        </Text>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  hint: {
    fontSize: fontSizes.caption,
  },
  input: {
    borderWidth: 1,
    fontSize: fontSizes.body,
    // See button.tsx: 44dp is the WCAG 2.5.5 target-size floor, not a token.
    minHeight: 44,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
  },
  label: {
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
  wrapper: {
    gap: spacing.xs,
  },
});
