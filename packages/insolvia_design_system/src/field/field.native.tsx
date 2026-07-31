// NATIVE LEAF — React Native primitives, faithful field wiring. Shares the id
// scheme and the describedby rule with the web leaf (field.props). Everything
// rendered is reimplemented: <label htmlFor> becomes a <Text> + the control's
// accessibilityLabel, <input> becomes <TextInput>, aria-invalid becomes
// accessibilityState/hint, and the render-as-select/textarea hatch has no RN
// analogue so it is dropped. Note how little of the a11y wiring transferred.
import * as React from 'react';
import {
  StyleSheet,
  Text,
  TextInput,
  View,
  type TextInputProps,
  type ViewProps,
} from 'react-native';

import { colors, radii, spacing } from '@insolvia-ai/tokens';
import {
  FieldContext,
  composeDescribedBy,
  useFieldContext,
  useFieldIds,
  type FieldContextValue,
  type FieldRootOwnProps,
} from './field.props';

const c = colors.light;

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

const FieldLabel = ({ children }: { children?: React.ReactNode }) => (
  <Text style={styles.label}>{children}</Text>
);

const FieldControl = (props: TextInputProps) => {
  const { invalid } = useFieldContext('Control');
  return (
    <TextInput
      accessibilityState={{ disabled: props.editable === false }}
      placeholderTextColor={c.muted}
      style={[styles.control, invalid ? styles.controlInvalid : null, props.style]}
      {...props}
    />
  );
};

const FieldDescription = ({ children }: { children?: React.ReactNode }) => (
  <Text style={styles.description}>{children}</Text>
);

const FieldError = ({ children }: { children?: React.ReactNode; match?: boolean }) => (
  <Text style={styles.error}>{children}</Text>
);

export const Field = {
  Root: FieldRoot,
  Label: FieldLabel,
  Control: FieldControl,
  Description: FieldDescription,
  Error: FieldError,
};

const styles = StyleSheet.create({
  root: { flexDirection: 'column', gap: spacing.xs },
  label: { fontSize: 14, fontWeight: '500', color: c.ink },
  control: {
    height: 40,
    width: '100%',
    borderRadius: radii.md,
    borderWidth: 1,
    borderColor: c.line,
    backgroundColor: c.card,
    paddingHorizontal: spacing.sm,
    fontSize: 14,
    color: c.ink,
  },
  controlInvalid: { borderColor: c.danger },
  description: { fontSize: 14, color: c.muted },
  error: { fontSize: 14, color: c.danger },
});
