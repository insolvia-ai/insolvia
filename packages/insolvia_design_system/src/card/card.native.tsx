// NATIVE LEAF — RN primitives over @insolvia-ai/tokens. Pure surface, so the
// port is layout-only: no state or a11y wiring to share beyond card.props.
import * as React from 'react';
import { StyleSheet, Text, View, type ViewProps } from 'react-native';

import { colors, radii, spacing } from '@insolvia-ai/tokens';

import type { CardElevation } from './card.props';

const c = colors.light;

export interface CardProps extends ViewProps {
  elevation?: CardElevation;
}

const CardRoot = ({ elevation = 'flat', style, ...props }: CardProps) => (
  <View style={[styles.root, elevation === 'raised' ? styles.raised : null, style]} {...props} />
);

const CardTitle = ({ children }: { children?: React.ReactNode }) => (
  <Text accessibilityRole="header" style={styles.title}>
    {children}
  </Text>
);

const CardBody = ({ children }: { children?: React.ReactNode }) => (
  <Text style={styles.body}>{children}</Text>
);

const CardFooter = ({ style, ...props }: ViewProps) => (
  <View style={[styles.footer, style]} {...props} />
);

export const Card = {
  Root: CardRoot,
  Title: CardTitle,
  Body: CardBody,
  Footer: CardFooter,
};

const styles = StyleSheet.create({
  root: {
    flexDirection: 'column',
    gap: spacing.sm,
    borderRadius: radii.lg,
    borderWidth: 1,
    borderColor: c.line,
    backgroundColor: c.card,
    padding: spacing.lg,
  },
  raised: {
    shadowColor: '#000',
    shadowOpacity: 0.1,
    shadowRadius: 6,
    shadowOffset: { width: 0, height: 4 },
    elevation: 3,
  },
  title: { fontSize: 18, fontWeight: '600', color: c.ink },
  body: { fontSize: 14, color: c.muted },
  footer: { flexDirection: 'row', alignItems: 'center', gap: spacing.sm, paddingTop: spacing.sm },
});
