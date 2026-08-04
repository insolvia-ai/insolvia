// NATIVE LEAF — React Native primitives over the shared machine
// (dialog.props). RN's Modal supplies what the web leaf builds by hand:
// portal-like hosting, focus containment, and hardware dismissal
// (onRequestClose — the Android back button; react-native-web maps it to
// Escape on app-web). The scrim is rendered BY the popup inside the Modal —
// a sibling part outside the Modal element tree could never overlay it — so
// the Backdrop part here renders nothing and exists only so cross-platform
// call sites compose identically. Colors resolve from useNativeColors() at
// render time; StyleSheet.create keeps scheme-independent layout only.
import * as React from 'react';
import { Modal, Pressable, StyleSheet, Text, View, type ViewProps } from 'react-native';

import { radii, spacing } from '@insolvia-ai/tokens';

import { useNativeColors } from '../lib/native-theme';
import {
  DialogRootContext,
  useDialogRootContext,
  useDialogState,
  type DialogRootOwnProps,
} from './dialog.props';

export interface DialogRootProps extends DialogRootOwnProps {
  children?: React.ReactNode;
}

// Renders no element of its own — it is the state owner and context provider.
const DialogRoot = ({ open, defaultOpen, onOpenChange, children }: DialogRootProps) => {
  const ctx = useDialogState(open, defaultOpen, onOpenChange);
  return <DialogRootContext.Provider value={ctx}>{children}</DialogRootContext.Provider>;
};

const DialogTrigger = ({ children }: { children?: React.ReactNode }) => {
  const { open, setOpen } = useDialogRootContext('Trigger');
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

// The Modal owns the overlay on native, so a standalone backdrop has nothing
// to render — the popup draws the scrim (and its tap-to-dismiss Pressable)
// inside the Modal. Kept as a part so the same JSX composes on both leaves.
const DialogBackdrop = (_props: { children?: React.ReactNode }) => null;

export type DialogPopupProps = ViewProps;

const DialogPopup = ({ children, style, ...props }: DialogPopupProps) => {
  const { open, setOpen, titleId, descriptionId } = useDialogRootContext('Popup');
  const c = useNativeColors();

  // Same child-presence scan as Field.Root: the card only points at the
  // Title/Description ids when those parts are actually rendered — a dangling
  // reference is its own a11y defect. Compares against THIS leaf's identities.
  let hasTitle = false;
  let hasDescription = false;
  React.Children.forEach(children, (child) => {
    if (!React.isValidElement(child)) return;
    if (child.type === DialogTitle) hasTitle = true;
    if (child.type === DialogDescription) hasDescription = true;
  });

  // The Modal's own container IS the dialog element: react-native-web renders
  // it with role="dialog" and aria-modal, and forwards extra props onto that
  // same element — so the labelling belongs on <Modal>, not on the inner card
  // (a second role="dialog" inside a dialog would be its own a11y defect).
  // `aria-labelledby` is in ModalProps (it includes ViewProps);
  // `aria-describedby` is not — it is web-only, and react-native-web forwards
  // it to the DOM regardless. OMITTED, not set to undefined, when the
  // description is absent. The cast is contained and documented where the
  // semantics live (same shape as Field's native leaf).
  const modalAria = {
    ...(hasDescription ? { 'aria-describedby': descriptionId } : {}),
  } as Partial<React.ComponentProps<typeof Modal>>;

  return (
    <Modal
      transparent
      visible={open}
      onRequestClose={() => setOpen(false)}
      aria-labelledby={hasTitle ? titleId : undefined}
      {...modalAria}
    >
      {/* `${c.ink}80` is the ink token at ~50% alpha (#rrggbbaa) — the scrim
          stays scheme-aware without inventing a new token. */}
      <View style={[styles.overlay, { backgroundColor: `${c.ink}80` }]}>
        <Pressable
          accessible={false}
          style={StyleSheet.absoluteFill}
          onPress={() => setOpen(false)}
        />
        <View style={[styles.card, { backgroundColor: c.card }, style]} {...props}>
          {children}
        </View>
      </View>
    </Modal>
  );
};

const DialogTitle = ({ children }: { children?: React.ReactNode }) => {
  const { titleId } = useDialogRootContext('Title');
  const c = useNativeColors();
  return (
    <Text nativeID={titleId} accessibilityRole="header" style={[styles.title, { color: c.ink }]}>
      {children}
    </Text>
  );
};

const DialogDescription = ({ children }: { children?: React.ReactNode }) => {
  const { descriptionId } = useDialogRootContext('Description');
  const c = useNativeColors();
  return (
    <Text nativeID={descriptionId} style={[styles.description, { color: c.muted }]}>
      {children}
    </Text>
  );
};

const DialogClose = ({ children }: { children?: React.ReactNode }) => {
  const { setOpen } = useDialogRootContext('Close');
  const c = useNativeColors();
  return (
    <Pressable accessibilityRole="button" onPress={() => setOpen(false)} style={styles.close}>
      <Text style={[styles.closeLabel, { color: c.ink }]}>{children}</Text>
    </Pressable>
  );
};

export const Dialog = {
  Root: DialogRoot,
  Trigger: DialogTrigger,
  Backdrop: DialogBackdrop,
  Popup: DialogPopup,
  Title: DialogTitle,
  Description: DialogDescription,
  Close: DialogClose,
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
