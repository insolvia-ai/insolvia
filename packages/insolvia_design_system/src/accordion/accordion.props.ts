// SHARED — imports `react` only (state/context/ids). No react-dom, no
// react-native. This is the accordion's entire behavior model: which items are
// open, how a toggle mutates that, and the per-item id derivation. Both leaves
// consume it verbatim; they differ only in the elements and a11y wiring they
// render around it.
import * as React from 'react';

export interface AccordionRootContextValue {
  isOpen: (value: string) => boolean;
  toggle: (value: string) => void;
}

export const AccordionRootContext = React.createContext<AccordionRootContextValue | null>(null);

export function useAccordionRootContext(part: string): AccordionRootContextValue {
  const ctx = React.useContext(AccordionRootContext);
  if (!ctx) throw new Error(`Accordion.${part} must be rendered inside <Accordion.Root>`);
  return ctx;
}

export interface AccordionItemContextValue {
  value: string;
  open: boolean;
  triggerId: string;
  panelId: string;
}

export const AccordionItemContext = React.createContext<AccordionItemContextValue | null>(null);

export function useAccordionItemContext(part: string): AccordionItemContextValue {
  const ctx = React.useContext(AccordionItemContext);
  if (!ctx) throw new Error(`Accordion.${part} must be rendered inside <Accordion.Item>`);
  return ctx;
}

export interface AccordionRootOwnProps {
  /** Items open on first render. Uncontrolled; omit for an all-closed start. */
  defaultValue?: string[];
  /** When false, opening one item closes the others. Defaults to true. */
  openMultiple?: boolean;
}

/**
 * The open/close state machine — pure React, identical on both platforms.
 * Multiple panels may be open at once unless `openMultiple` is false.
 */
export function useAccordionState(
  defaultValue: string[] | undefined,
  openMultiple: boolean,
): AccordionRootContextValue {
  const [openValues, setOpenValues] = React.useState<string[]>(defaultValue ?? []);

  const isOpen = React.useCallback((value: string) => openValues.includes(value), [openValues]);

  const toggle = React.useCallback(
    (value: string) => {
      setOpenValues((current) => {
        const isCurrentlyOpen = current.includes(value);
        if (openMultiple) {
          return isCurrentlyOpen ? current.filter((v) => v !== value) : [...current, value];
        }
        return isCurrentlyOpen ? [] : [value];
      });
    },
    [openMultiple],
  );

  return React.useMemo(() => ({ isOpen, toggle }), [isOpen, toggle]);
}

/** Derive an item's stable trigger/panel ids and its current open state. */
export function useAccordionItemState(
  value: string,
  isOpen: (value: string) => boolean,
): AccordionItemContextValue {
  const id = React.useId();
  return { value, open: isOpen(value), triggerId: `${id}-trigger`, panelId: `${id}-panel` };
}
