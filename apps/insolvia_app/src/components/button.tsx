import { Pressable, StyleSheet, Text } from 'react-native';
import type { StyleProp, ViewStyle } from 'react-native';

import { fontSizes, spacing, useTheme } from '@/theme';

/** Visual emphasis, carried over from the Flutter `AppButtonVariant`. */
export type ButtonVariant = 'primary' | 'secondary';

export interface ButtonProps {
  label: string;
  onPress: () => void;
  variant?: ButtonVariant;

  /**
   * A decorative trailing glyph, e.g. `'→'` where the Flutter button took
   * `Icons.arrow_forward`.
   *
   * A plain string rather than an icon component: no icon font or SVG set is
   * installed, and adding one to draw an arrow would cost bundle weight for
   * nothing. It is rendered `aria-hidden`, so it never reaches the accessible
   * name — `label` is the whole name.
   */
  icon?: string;

  disabled?: boolean;
  style?: StyleProp<ViewStyle>;
}

/**
 * The Insolvia button.
 *
 * `Pressable role="button"` is what react-native-web maps to a real
 * `<button type="button">` — keyboard-focusable, Enter/Space activated, and
 * announced as a button — rather than a clickable `<div>`. All color comes from
 * the theme; the pressed state uses the tokens' pre-computed `primaryActive`,
 * the same value marketing's `color-mix()` derives, so the two stacks land on
 * the same pixel.
 */
export function Button({
  label,
  onPress,
  variant = 'primary',
  icon,
  disabled = false,
  style,
}: ButtonProps) {
  const theme = useTheme();
  const primary = variant === 'primary';

  return (
    <Pressable
      role="button"
      // The accessible name is the label and only the label: `icon` is a
      // decorative glyph, and leaving the name to be composed from the children
      // would make it "Start a case →". The visible text is the label itself, so
      // this satisfies WCAG 2.5.3 (Label in Name) rather than working around it.
      aria-label={label}
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [
        styles.base,
        { borderRadius: theme.radii.md, paddingHorizontal: theme.spacing.lg },
        primary
          ? {
              backgroundColor: pressed ? theme.colors.primaryActive : theme.colors.primary,
              borderColor: pressed ? theme.colors.primaryActive : theme.colors.primary,
            }
          : {
              backgroundColor: pressed ? theme.colors.surfaceAlt : 'transparent',
              borderColor: theme.colors.line,
            },
        disabled ? styles.disabled : null,
        style,
      ]}
    >
      <Text
        style={[
          styles.label,
          {
            color: primary ? theme.colors.primaryText : theme.colors.primary,
            fontFamily: theme.typography.body,
          },
        ]}
      >
        {label}
      </Text>
      {icon === undefined ? null : (
        <Text
          aria-hidden
          style={[
            styles.icon,
            { color: primary ? theme.colors.primaryText : theme.colors.primary },
          ]}
        >
          {icon}
        </Text>
      )}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  base: {
    alignItems: 'center',
    alignSelf: 'flex-start',
    borderWidth: 1,
    flexDirection: 'row',
    gap: spacing.sm,
    justifyContent: 'center',
    // Not a design token: 44dp is the WCAG 2.5.5 / platform minimum touch
    // target, which is a floor rather than a step on the spacing scale.
    minHeight: 44,
    paddingVertical: spacing.sm,
  },
  disabled: {
    opacity: 0.5,
  },
  icon: {
    fontSize: fontSizes.label,
  },
  label: {
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
});
