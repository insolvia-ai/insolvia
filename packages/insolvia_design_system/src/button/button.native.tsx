// NATIVE LEAF — React Native primitives over @insolvia-ai/tokens. Metro resolves
// this. It imports `react-native`, which is exactly why the web build must never
// pick a .native leaf: this file is the falsification probe for the bundle grep.
import * as React from 'react';
import { Pressable, StyleSheet, Text, type PressableProps } from 'react-native';

import { colors, radii, spacing } from '@insolvia-ai/tokens';

import type { ButtonIntent, ButtonSize } from './button.props';

export interface ButtonProps extends PressableProps {
  intent?: ButtonIntent;
  size?: ButtonSize;
  children?: React.ReactNode;
}

const c = colors.light;

const intentBg: Record<ButtonIntent, string> = {
  primary: c.primary,
  secondary: c.surfaceAlt,
  ghost: 'transparent',
};

const intentText: Record<ButtonIntent, string> = {
  primary: c.primaryText,
  secondary: c.ink,
  ghost: c.ink,
};

const sizeHeight: Record<ButtonSize, number> = { sm: 32, md: 40, lg: 48 };
const sizePadX: Record<ButtonSize, number> = { sm: spacing.md, md: spacing.md, lg: spacing.lg };
const sizeText: Record<ButtonSize, number> = { sm: 14, md: 14, lg: 16 };

export function Button({
  intent = 'primary',
  size = 'md',
  children,
  disabled,
  style,
  ...props
}: ButtonProps) {
  return (
    <Pressable
      accessibilityRole="button"
      disabled={disabled ?? undefined}
      style={(state) => [
        styles.base,
        {
          height: sizeHeight[size],
          paddingHorizontal: sizePadX[size],
          backgroundColor: intentBg[intent],
          opacity: disabled ? 0.5 : state.pressed ? 0.9 : 1,
        },
        typeof style === 'function' ? style(state) : style,
      ]}
      {...props}
    >
      <Text style={[styles.label, { color: intentText[intent], fontSize: sizeText[size] }]}>
        {children}
      </Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  base: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: radii.md,
  },
  label: {
    fontWeight: '500',
  },
});
