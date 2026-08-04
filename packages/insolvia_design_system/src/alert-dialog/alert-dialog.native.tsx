// NATIVE LEAF — React Native primitives, faithful alert dialog. Shares the
// dialog's state machine and ids (via alert-dialog.props); the rendered markup
// deliberately DUPLICATES dialog.native.tsx rather than sharing a helper: the
// leaves differ exactly where the alert contract lives — role="alertdialog"
// and NO tap-outside dismissal (the scrim here has no Pressable).
//
// One documented exception to "no implicit dismissal": Modal.onRequestClose
// still closes, because it is the Android hardware back button (Escape on
// app-web via react-native-web) and the platform requires that back never
// trap the user. Treat it as the user declining — a caller needing a
// confirmed choice must handle onOpenChange(false) accordingly.
import * as React from 'react';
import { Modal, Pressable, StyleSheet, Text, View, type ViewProps } from 'react-native';

import { radii, spacing } from '@insolvia-ai/tokens';

import { useNativeColors } from '../lib/native-theme';
import {
  AlertDialogRootContext,
  useAlertDialogRootContext,
  useAlertDialogState,
  type AlertDialogRootOwnProps,
} from './alert-dialog.props';

export interface AlertDialogRootProps extends AlertDialogRootOwnProps {
  children?: React.ReactNode;
}

// Renders no element of its own — it is the state owner and context provider.
const AlertDialogRoot = ({ open, defaultOpen, onOpenChange, children }: AlertDialogRootProps) => {
  const ctx = useAlertDialogState(open, defaultOpen, onOpenChange);
  return <AlertDialogRootContext.Provider value={ctx}>{children}</AlertDialogRootContext.Provider>;
};

const AlertDialogTrigger = ({ children }: { children?: React.ReactNode }) => {
  const { open, setOpen } = useAlertDialogRootContext('Trigger');
  const c = useNativeColors();
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ expanded: open }}
      onPress={() => setOpen(true)}
      style={styles.trigger}
    >
      <Text style={[styles.triggerLabel, { color: c.ink }]}>{children}</Text>
    </Pressable>
  );
};

// The Modal owns the overlay on native; the popup draws the scrim inside it.
// Kept as a part so the same JSX composes on both leaves (see dialog.native).
const AlertDialogBackdrop = (_props: { children?: React.ReactNode }) => null;

export type AlertDialogPopupProps = ViewProps;

const AlertDialogPopup = ({ children, style, ...props }: AlertDialogPopupProps) => {
  const { open, setOpen, titleId, descriptionId } = useAlertDialogRootContext('Popup');
  const c = useNativeColors();

  // Same child-presence scan as Field.Root — point only at rendered parts.
  let hasTitle = false;
  let hasDescription = false;
  React.Children.forEach(children, (child) => {
    if (!React.isValidElement(child)) return;
    if (child.type === AlertDialogTitle) hasTitle = true;
    if (child.type === AlertDialogDescription) hasDescription = true;
  });

  // `aria-describedby` is web-only (not in RN's types); react-native-web
  // forwards it to the DOM. OMITTED, not undefined, when absent — the same
  // contained, documented cast Field's native leaf uses.
  //
  // Unlike dialog.native, the role and labels sit on the CARD, not the Modal:
  // react-native-web hard-codes role="dialog" on the Modal container and
  // ignores a role passed in, so "alertdialog" can only live on the card.
  const webAria = {
    ...(hasDescription ? { 'aria-describedby': descriptionId } : {}),
  } as ViewProps;

  return (
    // onRequestClose still closes — Android back must not trap the user; see
    // the header comment. There is deliberately NO Pressable on the scrim.
    <Modal transparent visible={open} onRequestClose={() => setOpen(false)}>
      {/* `${c.ink}80` is the ink token at ~50% alpha (#rrggbbaa) — the scrim
          stays scheme-aware without inventing a new token. */}
      <View style={[styles.overlay, { backgroundColor: `${c.ink}80` }]}>
        <View
          role="alertdialog"
          aria-modal
          aria-labelledby={hasTitle ? titleId : undefined}
          {...webAria}
          style={[styles.card, { backgroundColor: c.card }, style]}
          {...props}
        >
          {children}
        </View>
      </View>
    </Modal>
  );
};

const AlertDialogTitle = ({ children }: { children?: React.ReactNode }) => {
  const { titleId } = useAlertDialogRootContext('Title');
  const c = useNativeColors();
  return (
    <Text nativeID={titleId} accessibilityRole="header" style={[styles.title, { color: c.ink }]}>
      {children}
    </Text>
  );
};

const AlertDialogDescription = ({ children }: { children?: React.ReactNode }) => {
  const { descriptionId } = useAlertDialogRootContext('Description');
  const c = useNativeColors();
  return (
    <Text nativeID={descriptionId} style={[styles.description, { color: c.muted }]}>
      {children}
    </Text>
  );
};

const AlertDialogClose = ({ children }: { children?: React.ReactNode }) => {
  const { setOpen } = useAlertDialogRootContext('Close');
  const c = useNativeColors();
  return (
    <Pressable accessibilityRole="button" onPress={() => setOpen(false)} style={styles.close}>
      <Text style={[styles.closeLabel, { color: c.ink }]}>{children}</Text>
    </Pressable>
  );
};

export const AlertDialog = {
  Root: AlertDialogRoot,
  Trigger: AlertDialogTrigger,
  Backdrop: AlertDialogBackdrop,
  Popup: AlertDialogPopup,
  Title: AlertDialogTitle,
  Description: AlertDialogDescription,
  Close: AlertDialogClose,
};

const styles = StyleSheet.create({
  trigger: { alignSelf: 'flex-start' },
  triggerLabel: { fontSize: 16, fontWeight: '500' },
  overlay: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    padding: spacing.lg,
  },
  card: {
    width: '100%',
    maxWidth: 448, // the web leaf's max-w-md (28rem)
    borderRadius: radii.lg,
    padding: spacing.lg,
    gap: spacing.md,
  },
  title: { fontSize: 18, fontWeight: '600' },
  description: { fontSize: 14 },
  close: { alignSelf: 'flex-start', paddingVertical: spacing.xs },
  closeLabel: { fontSize: 14, fontWeight: '500' },
});
