// NATIVE LEAF — RN primitives over @insolvia-ai/tokens. Pure surface, so the
// port is layout-only: no state or a11y wiring to share beyond card.props.
// Colors come from useNativeColors() at render time (scheme-aware); only the
// scheme-independent layout sits in StyleSheet.create.
import * as React from 'react';
import { StyleSheet, Text, View, type ViewProps } from 'react-native';

import { radii, spacing } from '@insolvia-ai/tokens';

import { useNativeColors } from '../lib/native-theme';
import type { CardElevation } from './card.props';

export interface CardProps extends ViewProps {
  elevation?: CardElevation;
}

const CardRoot = ({ elevation = 'flat', style, ...props }: CardProps) => {
  const c = useNativeColors();
  return (
    <View
      style={[
        styles.root,
        { borderColor: c.line, backgroundColor: c.card },
        elevation === 'raised' ? styles.raised : null,
        style,
      ]}
      {...props}
    />
  );
};

const CardTitle = ({ children }: { children?: React.ReactNode }) => {
  const c = useNativeColors();
  return (
    <Text accessibilityRole="header" style={[styles.title, { color: c.ink }]}>
      {children}
    </Text>
  );
};

const CardBody = ({ children }: { children?: React.ReactNode }) => {
  const c = useNativeColors();
  return <Text style={[styles.body, { color: c.muted }]}>{children}</Text>;
};

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
    padding: spacing.lg,
  },
  raised: {
    shadowColor: '#000',
    shadowOpacity: 0.1,
    shadowRadius: 6,
    shadowOffset: { width: 0, height: 4 },
    elevation: 3,
  },
  title: { fontSize: 18, fontWeight: '600' },
  body: { fontSize: 14 },
  footer: { flexDirection: 'row', alignItems: 'center', gap: spacing.sm, paddingTop: spacing.sm },
});
