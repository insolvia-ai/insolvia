// NATIVE LEAF — simpler layout port. Anchors become Pressables the host wires
// to its navigator; this leaf only carries the visual shell + a11y roles.
import * as React from 'react';
import { Pressable, StyleSheet, Text, View, type ViewProps } from 'react-native';

import { colors, spacing } from '@insolvia-ai/tokens';

import type { NavBarLinkOwnProps } from './nav-bar.props';

const c = colors.light;

const NavBarRoot = ({ style, ...props }: ViewProps) => (
  <View accessibilityRole="header" style={[styles.root, style]} {...props} />
);

const NavBarBrand = ({ children }: { children?: React.ReactNode }) => (
  <Text style={styles.brand}>{children}</Text>
);

const NavBarLinks = ({ style, ...props }: ViewProps) => (
  <View style={[styles.links, style]} {...props} />
);

interface NativeLinkProps extends NavBarLinkOwnProps {
  children?: React.ReactNode;
  onPress?: () => void;
}

const NavBarLink = ({ active = false, children, onPress }: NativeLinkProps) => (
  <Pressable accessibilityRole="link" onPress={onPress}>
    <Text style={[styles.link, active ? styles.linkActive : null]}>{children}</Text>
  </Pressable>
);

const NavBarActions = ({ style, ...props }: ViewProps) => (
  <View style={[styles.actions, style]} {...props} />
);

export const NavBar = {
  Root: NavBarRoot,
  Brand: NavBarBrand,
  Links: NavBarLinks,
  Link: NavBarLink,
  Actions: NavBarActions,
};

const styles = StyleSheet.create({
  root: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: spacing.lg,
    borderBottomWidth: 1,
    borderBottomColor: c.line,
    backgroundColor: c.bg,
    paddingHorizontal: spacing.lg,
    paddingVertical: spacing.md,
  },
  brand: { fontSize: 18, fontWeight: '600', color: c.brand },
  links: { flexDirection: 'row', alignItems: 'center', gap: spacing.lg },
  link: { fontSize: 14, color: c.muted },
  linkActive: { fontWeight: '500', color: c.ink },
  actions: { flexDirection: 'row', alignItems: 'center', gap: spacing.sm },
});
