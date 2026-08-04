// NATIVE LEAF — React Native primitives, faithful single-item disclosure.
// Shares the exact state model with the web leaf (collapsible.props); what is
// reimplemented here is every rendered element and the a11y surface: RN has
// no <button>/region, so the trigger maps onto Pressable +
// accessibilityState/accessibilityRole, and the collapsed panel is unmounted
// rather than height-animated (no CSS grid trick in RN) — the same idiom
// accordion.native.tsx uses for its panel.
import * as React from 'react';
import { Pressable, StyleSheet, Text, View, type ViewProps } from 'react-native';

import { spacing } from '@insolvia-ai/tokens';

import { useNativeColors } from '../lib/native-theme';
import {
  CollapsibleRootContext,
  useCollapsibleRootContext,
  useCollapsibleState,
  type CollapsibleRootOwnProps,
} from './collapsible.props';

export interface CollapsibleRootProps extends ViewProps, CollapsibleRootOwnProps {}

const CollapsibleRoot = ({
  open,
  defaultOpen = false,
  onOpenChange,
  disabled = false,
  children,
  style,
  ...props
}: CollapsibleRootProps) => {
  const ctx = useCollapsibleState(open, defaultOpen, onOpenChange, disabled);
  return (
    <CollapsibleRootContext.Provider value={ctx}>
      <View style={[styles.root, style]} {...props}>
        {children}
      </View>
    </CollapsibleRootContext.Provider>
  );
};

const CollapsibleTrigger = ({ children }: { children?: React.ReactNode }) => {
  const { toggle, open, disabled } = useCollapsibleRootContext('Trigger');
  const c = useNativeColors();
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ expanded: open, disabled }}
      disabled={disabled}
      onPress={toggle}
      style={styles.trigger}
    >
      <Text style={[styles.triggerLabel, { color: disabled ? c.muted : c.ink }]}>{children}</Text>
    </Pressable>
  );
};

const CollapsiblePanel = ({ children }: { children?: React.ReactNode }) => {
  const { open } = useCollapsibleRootContext('Panel');
  const c = useNativeColors();
  if (!open) return null; // unmount rather than height-animate — no CSS grid trick in RN
  return (
    <View style={styles.panel}>
      <Text style={[styles.panelText, { color: c.muted }]}>{children}</Text>
    </View>
  );
};

export const Collapsible = {
  Root: CollapsibleRoot,
  Trigger: CollapsibleTrigger,
  Panel: CollapsiblePanel,
};

const styles = StyleSheet.create({
  root: { flexDirection: 'column' },
  trigger: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: spacing.sm,
  },
  triggerLabel: { fontSize: 14, fontWeight: '500' },
  panel: { paddingBottom: spacing.sm },
  panelText: { fontSize: 14 },
});
