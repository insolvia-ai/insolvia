// NATIVE LEAF — simpler layout port. Anchors become Pressables the host wires
// to its navigator; this leaf only carries the visual shell + a11y roles.
// Colors come from useNativeColors() at render time (scheme-aware); only
// scheme-independent layout is static.
import * as React from 'react';
import { Pressable, StyleSheet, Text, View, type ViewProps } from 'react-native';

import { spacing } from '@insolvia-ai/tokens';

import { useNativeColors } from '../lib/native-theme';
import type { NavBarLinkOwnProps } from './nav-bar.props';

const NavBarRoot = ({ style, ...props }: ViewProps) => {
  const c = useNativeColors();
  return (
    <View
      accessibilityRole="header"
      style={[styles.root, { borderBottomColor: c.line, backgroundColor: c.bg }, style]}
      {...props}
    />
  );
};

const NavBarBrand = ({ children }: { children?: React.ReactNode }) => {
  const c = useNativeColors();
  return <Text style={[styles.brand, { color: c.brand }]}>{children}</Text>;
};

const NavBarLinks = ({ style, ...props }: ViewProps) => (
  <View style={[styles.links, style]} {...props} />
);

interface NativeLinkProps extends NavBarLinkOwnProps {
  children?: React.ReactNode;
  onPress?: () => void;
}

const NavBarLink = ({ active = false, children, onPress }: NativeLinkProps) => {
  const c = useNativeColors();
  return (
    <Pressable accessibilityRole="link" onPress={onPress}>
      <Text
        style={[
          styles.link,
          { color: active ? c.ink : c.muted },
          active ? styles.linkActive : null,
        ]}
      >
        {children}
      </Text>
    </Pressable>
  );
};

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
    paddingHorizontal: spacing.lg,
    paddingVertical: spacing.md,
  },
  brand: { fontSize: 18, fontWeight: '600' },
  links: { flexDirection: 'row', alignItems: 'center', gap: spacing.lg },
  link: { fontSize: 14 },
  linkActive: { fontWeight: '500' },
  actions: { flexDirection: 'row', alignItems: 'center', gap: spacing.sm },
});
