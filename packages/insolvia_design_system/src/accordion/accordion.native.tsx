// NATIVE LEAF — React Native primitives, faithful accordion. Shares the exact
// state model with the web leaf (accordion.props); what is reimplemented here is
// every rendered element and the a11y surface: RN has no <button>/<h3>/region,
// so behavior maps onto Pressable + accessibilityState/accessibilityRole, and
// the collapsed panel is unmounted rather than height-animated.
import * as React from 'react';
import { Pressable, StyleSheet, Text, View, type ViewProps } from 'react-native';

import { colors, spacing } from '@insolvia-ai/tokens';
import {
  AccordionItemContext,
  AccordionRootContext,
  useAccordionItemState,
  useAccordionItemContext,
  useAccordionRootContext,
  useAccordionState,
  type AccordionRootOwnProps,
} from './accordion.props';

const c = colors.light;

export interface AccordionRootProps extends ViewProps, AccordionRootOwnProps {}

const AccordionRoot = ({
  defaultValue,
  openMultiple = true,
  children,
  style,
  ...props
}: AccordionRootProps) => {
  const ctx = useAccordionState(defaultValue, openMultiple);
  return (
    <AccordionRootContext.Provider value={ctx}>
      <View style={[styles.root, style]} {...props}>
        {children}
      </View>
    </AccordionRootContext.Provider>
  );
};

export interface AccordionItemProps extends ViewProps {
  value: string;
}

const AccordionItem = ({ value, children, style, ...props }: AccordionItemProps) => {
  const { isOpen } = useAccordionRootContext('Item');
  const ctx = useAccordionItemState(value, isOpen);
  return (
    <AccordionItemContext.Provider value={ctx}>
      <View style={[styles.item, style]} {...props}>
        {children}
      </View>
    </AccordionItemContext.Provider>
  );
};

const AccordionHeader = ({ children }: { children?: React.ReactNode }) => (
  <View accessibilityRole="header">{children}</View>
);

const AccordionTrigger = ({ children }: { children?: React.ReactNode }) => {
  const { toggle } = useAccordionRootContext('Trigger');
  const { value, open } = useAccordionItemContext('Trigger');
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ expanded: open }}
      onPress={() => toggle(value)}
      style={styles.trigger}
    >
      <Text style={styles.triggerLabel}>{children}</Text>
    </Pressable>
  );
};

const AccordionPanel = ({ children }: { children?: React.ReactNode }) => {
  const { open } = useAccordionItemContext('Panel');
  if (!open) return null;
  return (
    <View style={styles.panel}>
      <Text style={styles.panelText}>{children}</Text>
    </View>
  );
};

export const Accordion = {
  Root: AccordionRoot,
  Item: AccordionItem,
  Header: AccordionHeader,
  Trigger: AccordionTrigger,
  Panel: AccordionPanel,
};

const styles = StyleSheet.create({
  root: { flexDirection: 'column' },
  item: { borderBottomWidth: 1, borderBottomColor: c.line },
  trigger: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: spacing.md,
  },
  triggerLabel: { fontSize: 16, fontWeight: '500', color: c.ink },
  panel: { paddingBottom: spacing.md },
  panelText: { fontSize: 14, color: c.muted },
});
