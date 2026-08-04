// NATIVE LEAF — React Native primitives, faithful tabs. Shares the exact
// state model with the web leaf (tabs.props); what is reimplemented here is
// every rendered element and the a11y surface: RN has no tablist/tab/tabpanel
// DOM roles or id/aria-* linkage, so Tab<->Panel pairing is done purely by
// `value` equality (no ids needed), and behavior maps onto
// Pressable + accessibilityRole="tab"/"tablist" + accessibilityState.selected.
// The inactive panel unmounts rather than animating — same call the web leaf
// makes, and there is no RN keyboard/arrow-key concept to reimplement at all
// (touch just presses the tab it wants).
import * as React from 'react';
import { Pressable, StyleSheet, Text, View, type ViewProps } from 'react-native';

import { spacing } from '@insolvia-ai/tokens';

import { useNativeColors } from '../lib/native-theme';
import {
  TabsRootContext,
  useTabsRootContext,
  useTabsState,
  type TabsRootOwnProps,
} from './tabs.props';

export interface TabsRootProps extends Omit<ViewProps, 'children'>, TabsRootOwnProps {
  children?: React.ReactNode;
}

const TabsRoot = ({
  value,
  defaultValue,
  onValueChange,
  children,
  style,
  ...props
}: TabsRootProps) => {
  const ctx = useTabsState(value, defaultValue, onValueChange);
  return (
    <TabsRootContext.Provider value={ctx}>
      <View style={[styles.root, style]} {...props}>
        {children}
      </View>
    </TabsRootContext.Provider>
  );
};

const TabsList = ({ children, style, ...props }: ViewProps) => (
  <View accessibilityRole="tablist" style={[styles.list, style]} {...props}>
    {children}
  </View>
);

export interface TabProps {
  /** Which panel this tab activates; matched against the same `value` on `Panel`. */
  value: string;
  children?: React.ReactNode;
}

const Tab = ({ value, children }: TabProps) => {
  const { value: activeValue, setValue } = useTabsRootContext('Tab');
  const active = activeValue === value;
  const c = useNativeColors();
  return (
    <Pressable
      accessibilityRole="tab"
      accessibilityState={{ selected: active }}
      // `accessibilityState.selected` is what real iOS/Android accessibility
      // reads. The react-native-web this package ships (app-web) does NOT
      // unpack `accessibilityState` into `aria-*` attributes for `selected` —
      // it only forwards flat `aria-*` props verbatim, the same gap Field's
      // native leaf documents for `aria-describedby`/`aria-invalid`. Both are
      // set so every platform this leaf actually renders on gets a correct
      // "selected" signal.
      aria-selected={active}
      onPress={() => setValue(value)}
      style={[styles.tab, { borderBottomColor: active ? c.primary : 'transparent' }]}
    >
      <Text style={[styles.tabLabel, { color: active ? c.ink : c.muted }]}>{children}</Text>
    </Pressable>
  );
};

export interface TabPanelProps {
  /** Which tab activates this panel; matched against the same `value` on `Tab`. */
  value: string;
  children?: React.ReactNode;
}

const TabPanel = ({ value, children }: TabPanelProps) => {
  const { value: activeValue } = useTabsRootContext('Panel');
  if (activeValue !== value) return null;
  // A plain View, like any other RN container — arbitrary content, not just
  // text, is the point of a panel, so it does not force-wrap `children` in a
  // `Text` (unlike Accordion's panel, which only ever holds prose). Raw text
  // still needs the caller's own `<Text>`, the same rule as everywhere else
  // in RN.
  return <View style={styles.panel}>{children}</View>;
};

export const Tabs = {
  Root: TabsRoot,
  List: TabsList,
  Tab,
  Panel: TabPanel,
};

const styles = StyleSheet.create({
  root: { flexDirection: 'column' },
  list: { flexDirection: 'row' },
  tab: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
    borderBottomWidth: 2,
  },
  tabLabel: { fontSize: 14, fontWeight: '500' },
  panel: { paddingVertical: spacing.md },
});
