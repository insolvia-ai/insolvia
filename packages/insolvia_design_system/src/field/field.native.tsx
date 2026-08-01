// NATIVE LEAF — React Native primitives, faithful field wiring. Shares the id
// scheme and the describedby rule with the web leaf (field.props), but the
// association INVERTS: the web leaf points the label at the control with
// <label htmlFor>; here the label Text carries nativeID={labelId} and the
// control points back at it with aria-labelledby — the pair react-native-web
// emits as a correctly associated label/input on app-web. aria-describedby is
// composed from ONLY the description/error ids actually rendered (each part
// carries its nativeID), aria-invalid mirrors the web leaf, and the
// render-as-select/textarea hatch has no RN analogue so it is dropped.
// Colors come from useNativeColors() at render time (scheme-aware); only
// scheme-independent layout is static in StyleSheet.create.
import * as React from 'react';
import {
  StyleSheet,
  Text,
  TextInput,
  View,
  type TextInputProps,
  type ViewProps,
} from 'react-native';

import { radii, spacing } from '@insolvia-ai/tokens';

import { useNativeColors } from '../lib/native-theme';
import {
  FieldContext,
  composeDescribedBy,
  useFieldContext,
  useFieldIds,
  type FieldContextValue,
  type FieldRootOwnProps,
} from './field.props';

export interface FieldRootProps extends Omit<ViewProps, 'children'>, FieldRootOwnProps {
  children?: React.ReactNode;
}

const FieldRoot = ({ name, invalid = false, children, style, ...props }: FieldRootProps) => {
  const ids = useFieldIds();

  let hasDescription = false;
  let hasError = false;
  React.Children.forEach(children, (child) => {
    if (!React.isValidElement(child)) return;
    if (child.type === FieldDescription) hasDescription = true;
    if (child.type === FieldError) hasError = true;
  });

  const ctx: FieldContextValue = {
    labelId: ids.labelId,
    controlId: ids.controlId,
    describedBy: composeDescribedBy(ids, hasDescription, hasError),
    invalid,
    name,
    descriptionId: ids.descriptionId,
    errorId: ids.errorId,
  };

  return (
    <FieldContext.Provider value={ctx}>
      <View style={[styles.root, style]} {...props}>
        {children}
      </View>
    </FieldContext.Provider>
  );
};

const FieldLabel = ({ children }: { children?: React.ReactNode }) => {
  const { labelId } = useFieldContext('Label');
  const c = useNativeColors();
  return (
    <Text nativeID={labelId} style={[styles.label, { color: c.ink }]}>
      {children}
    </Text>
  );
};

const FieldControl = ({ style, ...props }: TextInputProps) => {
  const { labelId, controlId, describedBy, invalid } = useFieldContext('Control');
  const c = useNativeColors();

  // `aria-labelledby` is in react-native's types; `aria-describedby` and
  // `aria-invalid` are not — they are web-only, and react-native-web forwards
  // them to the DOM regardless. Both are OMITTED, not set to undefined, when
  // inapplicable, so the control never points at an element that does not
  // exist and never claims a validity state the root did not set. The cast is
  // contained and documented where the semantics live — the same shape the
  // app used before adopting this leaf.
  const webAria = {
    ...(describedBy === undefined ? {} : { 'aria-describedby': describedBy }),
    ...(invalid ? { 'aria-invalid': true } : {}),
  } as TextInputProps;

  return (
    <TextInput
      nativeID={controlId}
      aria-labelledby={labelId}
      {...webAria}
      accessibilityState={{ disabled: props.editable === false }}
      placeholderTextColor={c.muted}
      style={[
        styles.control,
        { borderColor: invalid ? c.danger : c.line, backgroundColor: c.card, color: c.ink },
        style,
      ]}
      {...props}
    />
  );
};

const FieldDescription = ({ children }: { children?: React.ReactNode }) => {
  const { descriptionId } = useFieldContext('Description');
  const c = useNativeColors();
  return (
    <Text nativeID={descriptionId} style={[styles.description, { color: c.muted }]}>
      {children}
    </Text>
  );
};

const FieldError = ({ children }: { children?: React.ReactNode; match?: boolean }) => {
  const { errorId } = useFieldContext('Error');
  const c = useNativeColors();
  return (
    <Text nativeID={errorId} style={[styles.error, { color: c.danger }]}>
      {children}
    </Text>
  );
};

export const Field = {
  Root: FieldRoot,
  Label: FieldLabel,
  Control: FieldControl,
  Description: FieldDescription,
  Error: FieldError,
};

const styles = StyleSheet.create({
  root: { flexDirection: 'column', gap: spacing.xs },
  label: { fontSize: 14, fontWeight: '500' },
  control: {
    height: 40,
    width: '100%',
    borderRadius: radii.md,
    borderWidth: 1,
    paddingHorizontal: spacing.sm,
    fontSize: 14,
  },
  description: { fontSize: 14 },
  error: { fontSize: 14 },
});
