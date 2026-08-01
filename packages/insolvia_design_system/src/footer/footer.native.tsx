// NATIVE LEAF — simpler layout port. Colors come from useNativeColors() at
// render time (scheme-aware); only scheme-independent layout is static.
import * as React from 'react';
import { Pressable, StyleSheet, Text, View, type ViewProps } from 'react-native';

import { spacing } from '@insolvia-ai/tokens';

import { useNativeColors } from '../lib/native-theme';
import type { FooterGroupOwnProps } from './footer.props';

const FooterRoot = ({ style, ...props }: ViewProps) => {
  const c = useNativeColors();
  return (
    <View
      style={[styles.root, { borderTopColor: c.line, backgroundColor: c.surfaceAlt }, style]}
      {...props}
    />
  );
};

interface GroupProps extends FooterGroupOwnProps {
  children?: React.ReactNode;
}

const FooterGroup = ({ title, children }: GroupProps) => {
  const c = useNativeColors();
  return (
    <View accessibilityRole="summary" accessibilityLabel={title} style={styles.group}>
      <Text style={[styles.groupTitle, { color: c.ink }]}>{title}</Text>
      <View style={styles.groupLinks}>{children}</View>
    </View>
  );
};

const FooterLink = ({
  children,
  onPress,
}: {
  children?: React.ReactNode;
  onPress?: () => void;
}) => {
  const c = useNativeColors();
  return (
    <Pressable accessibilityRole="link" onPress={onPress}>
      <Text style={[styles.link, { color: c.muted }]}>{children}</Text>
    </Pressable>
  );
};

const FooterNote = ({ children }: { children?: React.ReactNode }) => {
  const c = useNativeColors();
  return <Text style={[styles.note, { borderTopColor: c.line, color: c.muted }]}>{children}</Text>;
};

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
    paddingHorizontal: spacing.lg,
    paddingVertical: spacing.xl,
  },
  group: { flexDirection: 'column', gap: spacing.sm },
  groupTitle: { fontSize: 14, fontWeight: '600' },
  groupLinks: { flexDirection: 'column', gap: spacing.xs },
  link: { fontSize: 14 },
  note: {
    borderTopWidth: 1,
    paddingTop: spacing.md,
    fontSize: 14,
  },
});
