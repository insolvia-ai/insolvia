import { StyleSheet, Text, View } from 'react-native';

import { environmentInfo, type AppEnvironment } from '@/config/environment';
import { fontSizes, spacing, useTheme } from '@/theme';

export interface EnvBadgeProps {
  env: AppEnvironment;
}

/**
 * A small pill showing the current environment, tinted by the brand accent —
 * what makes a staging build unmistakable at a glance.
 *
 * The brass accent tints the fill and the border, but the label is `ink`, not
 * accent: brass on a pale brass tint is ~1.9:1, far under WCAG AA for 12px text,
 * and the axe gate in `app-pr.yml` rightly fails on it. The pill still reads as
 * brass; only the letters are high-contrast — the standard accessible-chip
 * pattern (colored container, readable text).
 *
 * The accessible name spells the host out ("Staging environment,
 * staging-app.insolvia.ai") because the visible text is an all-caps
 * abbreviation, and the Flutter badge carried the host in a tooltip that has no
 * React Native equivalent.
 */
export function EnvBadge({ env }: EnvBadgeProps) {
  const theme = useTheme();
  const info = environmentInfo(env);

  return (
    <View
      accessible
      aria-label={`${info.label} environment, ${info.host}`}
      style={[
        styles.pill,
        {
          backgroundColor: withAlpha(theme.colors.accent, 0.14),
          borderColor: withAlpha(theme.colors.accent, 0.5),
          borderRadius: theme.radii.sm,
        },
      ]}
    >
      <Text style={[styles.label, { color: theme.colors.ink, fontFamily: theme.typography.body }]}>
        {info.label.toUpperCase()}
      </Text>
    </View>
  );
}

/**
 * Applies an alpha to a token color without inventing a new one.
 *
 * The Flutter badge used `Color.withValues(alpha:)` on the same accent token.
 * Tokens are `#RRGGBB` (or `#RRGGBBAA` for the dark hairline), and React Native
 * accepts 8-digit hex on every platform, so the alpha is appended rather than
 * blended — the underlying hue stays the token's.
 */
function withAlpha(color: string, alpha: number): string {
  const base = color.slice(0, 7);
  const byte = Math.round(Math.min(Math.max(alpha, 0), 1) * 255);
  return `${base}${byte.toString(16).padStart(2, '0')}`;
}

const styles = StyleSheet.create({
  label: {
    fontSize: fontSizes.caption,
    fontWeight: '600',
    letterSpacing: 1,
  },
  pill: {
    borderWidth: 1,
    paddingHorizontal: spacing.sm,
    paddingVertical: spacing.xs,
  },
});
