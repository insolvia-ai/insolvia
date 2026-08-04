// SHARED — imports `react` and the dialog's props module only (a props module
// importing another props module is fine: both are renderer-free, and the
// ESLint fence only bans renderers). The alert dialog IS the dialog's state
// machine and id scheme under a stricter dismissal contract — no backdrop
// click, no Escape; the user must pick an explicit option. That contract is
// leaf behavior, so nothing here re-implements state. It gets its OWN context
// instance so a Dialog nested inside an AlertDialog (or vice versa) can never
// cross-wire, and its own accessor so misuse errors name the right component.
import * as React from 'react';

import {
  useDialogState,
  type DialogContextValue,
  type DialogRootOwnProps,
} from '../dialog/dialog.props';

export type AlertDialogContextValue = DialogContextValue;
export type AlertDialogRootOwnProps = DialogRootOwnProps;

export const AlertDialogRootContext = React.createContext<AlertDialogContextValue | null>(null);

export function useAlertDialogRootContext(part: string): AlertDialogContextValue {
  const ctx = React.useContext(AlertDialogRootContext);
  if (!ctx) throw new Error(`AlertDialog.${part} must be rendered inside <AlertDialog.Root>`);
  return ctx;
}

/** The dialog's machine, verbatim — one state convention, two anatomies. */
export const useAlertDialogState = useDialogState;
