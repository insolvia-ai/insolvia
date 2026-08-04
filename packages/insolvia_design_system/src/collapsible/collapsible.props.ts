// SHARED — imports `react` only (state/context/ids), plus the shared
// controllable-state helper from src/lib. No react-dom, no react-native. This
// is the collapsible's entire behavior model: one open/closed boolean and the
// trigger/panel id pair. Unlike Accordion there is exactly one item — the
// root itself — so there is a single *RootContext and no *ItemContext split.
// Both leaves consume this verbatim; they differ only in the elements and
// a11y wiring rendered around it.
import * as React from 'react';

import { useControllableState } from '../lib/controllable';

export interface CollapsibleRootContextValue {
  open: boolean;
  disabled: boolean;
  toggle: () => void;
  triggerId: string;
  panelId: string;
}

export const CollapsibleRootContext = React.createContext<CollapsibleRootContextValue | null>(
  null,
);

export function useCollapsibleRootContext(part: string): CollapsibleRootContextValue {
  const ctx = React.useContext(CollapsibleRootContext);
  if (!ctx) throw new Error(`Collapsible.${part} must be rendered inside <Collapsible.Root>`);
  return ctx;
}

export interface CollapsibleRootOwnProps {
  /** Open state when controlled. Omit to let the component manage it internally. */
  open?: boolean;
  /** Open state on first render, uncontrolled. Defaults to closed. */
  defaultOpen?: boolean;
  /** Fires with the value the trigger wants, in both controlled and uncontrolled mode. */
  onOpenChange?: (open: boolean) => void;
  /** When true, the trigger cannot toggle the panel. Defaults to false. */
  disabled?: boolean;
}

/**
 * The open/close state machine — pure React, identical on both platforms.
 * Built on useControllableState (src/lib/controllable) so Root supports both
 * uncontrolled (`defaultOpen` + internal state) and controlled (`open` +
 * `onOpenChange`, parent-owned) usage, same as every other stateful component
 * in this package.
 */
export function useCollapsibleState(
  open: boolean | undefined,
  defaultOpen: boolean,
  onOpenChange: ((open: boolean) => void) | undefined,
  disabled: boolean,
): CollapsibleRootContextValue {
  const [value, setValue] = useControllableState<boolean>(open, defaultOpen, onOpenChange);
  const id = React.useId();

  const toggle = React.useCallback(() => {
    if (disabled) return;
    setValue(!value);
  }, [disabled, value, setValue]);

  return React.useMemo(
    () => ({
      open: value,
      disabled,
      toggle,
      triggerId: `${id}-trigger`,
      panelId: `${id}-panel`,
    }),
    [value, disabled, toggle, id],
  );
}
