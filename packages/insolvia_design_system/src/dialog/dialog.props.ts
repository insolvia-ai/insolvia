// SHARED — imports `react` and the shared controllable helper only; no
// react-dom, no react-native (ESLint-fenced). This is the dialog's entire
// platform-agnostic surface: the open/close state machine (controlled or
// uncontrolled, via useControllableState) and the title/description id
// derivation the popup's aria-labelledby/-describedby wiring points at.
// Everything that renders — portal + backdrop + focus trap on web, RN Modal on
// native — is platform behavior and lives in the leaves. The alert-dialog
// props module imports from here: same machine, same ids, different dismissal
// contract (which is leaf behavior, not state).
import * as React from 'react';

import { useControllableState } from '../lib/controllable';

export interface DialogContextValue {
  open: boolean;
  setOpen: (next: boolean) => void;
  titleId: string;
  descriptionId: string;
}

export const DialogRootContext = React.createContext<DialogContextValue | null>(null);

export function useDialogRootContext(part: string): DialogContextValue {
  const ctx = React.useContext(DialogRootContext);
  if (!ctx) throw new Error(`Dialog.${part} must be rendered inside <Dialog.Root>`);
  return ctx;
}

// The `| undefined` on each member is for `exactOptionalPropertyTypes` (on in
// tsconfig.base.json): both leaves forward these straight from their own
// props, where each is `T | undefined`, and that assignment is an error
// against a bare optional.
export interface DialogRootOwnProps {
  /** Controlled open state — the parent owns it and must re-render to change it. */
  open?: boolean | undefined;
  /** Open on first render. Uncontrolled; ignored while `open` is provided. */
  defaultOpen?: boolean | undefined;
  /** Fires with the state the dialog wants on every open/close request, in both modes. */
  onOpenChange?: ((open: boolean) => void) | undefined;
}

/**
 * The dialog's state machine plus its stable title/description ids — pure
 * React, identical on both platforms and reused verbatim by the alert dialog.
 * Controlled/uncontrolled behavior comes from the shared useControllableState
 * convention; ids follow the house `useId()`-plus-suffix rule.
 */
export function useDialogState(
  open: boolean | undefined,
  defaultOpen: boolean | undefined,
  onOpenChange: ((open: boolean) => void) | undefined,
): DialogContextValue {
  const [isOpen, setOpen] = useControllableState(open, defaultOpen ?? false, onOpenChange);
  const id = React.useId();
  return React.useMemo(
    () => ({ open: isOpen, setOpen, titleId: `${id}-title`, descriptionId: `${id}-description` }),
    [isOpen, setOpen, id],
  );
}
