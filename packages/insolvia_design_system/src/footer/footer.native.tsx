// NATIVE LEAF — simpler layout port.
import * as React from 'react';
import { Pressable, StyleSheet, Text, View, type ViewProps } from 'react-native';

import { colors, spacing } from '@insolvia-ai/tokens';

import type { FooterGroupOwnProps } from './footer.props';

const c = colors.light;

const FooterRoot = ({ style, ...props }: ViewProps) => (
  <View style={[styles.root, style]} {...props} />
);

interface GroupProps extends FooterGroupOwnProps {
  children?: React.ReactNode;
}

const FooterGroup = ({ title, children }: GroupProps) => (
  <View accessibilityRole="summary" accessibilityLabel={title} style={styles.group}>
    <Text style={styles.groupTitle}>{title}</Text>
    <View style={styles.groupLinks}>{children}</View>
  </View>
);

const FooterLink = ({
  children,
  onPress,
}: {
  children?: React.ReactNode;
  onPress?: () => void;
}) => (
  <Pressable accessibilityRole="link" onPress={onPress}>
    <Text style={styles.link}>{children}</Text>
  </Pressable>
);

const FooterNote = ({ children }: { children?: React.ReactNode }) => (
  <Text style={styles.note}>{children}</Text>
);

export const Footer = {
  Root: FooterRoot,
  Group: FooterGroup,
  Link: FooterLink,
  Note: FooterNote,
};

const styles = StyleSheet.create({
  root: {
    flexDirection: 'column',
    gap: spacing.lg,
    borderTopWidth: 1,
    borderTopColor: c.line,
    backgroundColor: c.surfaceAlt,
    paddingHorizontal: spacing.lg,
    paddingVertical: spacing.xl,
  },
  group: { flexDirection: 'column', gap: spacing.sm },
  groupTitle: { fontSize: 14, fontWeight: '600', color: c.ink },
  groupLinks: { flexDirection: 'column', gap: spacing.xs },
  link: { fontSize: 14, color: c.muted },
  note: {
    borderTopWidth: 1,
    borderTopColor: c.line,
    paddingTop: spacing.md,
    fontSize: 14,
    color: c.muted,
  },
});
