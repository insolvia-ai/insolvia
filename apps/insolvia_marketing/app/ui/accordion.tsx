import * as React from "react";

import { cn } from "~/lib/cn";
import { disabledStyles, focusRing } from "./styles";

// A hand-rolled accordion following the WAI-ARIA pattern, replacing Base UI's.
// Preserved from the previous behaviour: multiple panels may be open at once
// (each toggles independently), triggers are arrow-key navigable, and the panel
// animates its height open/closed.
//
// The panel stays in the DOM when closed (height collapsed via a grid-rows
// animation, not `display:none`), so its text is still crawlable — it is only
// made `inert` so it leaves the tab order and the accessibility tree while
// collapsed. `role="region"` + `aria-labelledby` name each open panel by its
// trigger; the trigger carries `aria-expanded` + `aria-controls`.

interface AccordionRootContextValue {
  isOpen: (value: string) => boolean;
  toggle: (value: string) => void;
  rootRef: React.RefObject<HTMLDivElement | null>;
}

const AccordionRootContext = React.createContext<AccordionRootContextValue | null>(null);

function useRootContext(part: string): AccordionRootContextValue {
  const ctx = React.useContext(AccordionRootContext);
  if (!ctx) throw new Error(`Accordion.${part} must be rendered inside <Accordion.Root>`);
  return ctx;
}

interface AccordionItemContextValue {
  value: string;
  open: boolean;
  triggerId: string;
  panelId: string;
}

const AccordionItemContext = React.createContext<AccordionItemContextValue | null>(null);

function useItemContext(part: string): AccordionItemContextValue {
  const ctx = React.useContext(AccordionItemContext);
  if (!ctx) throw new Error(`Accordion.${part} must be rendered inside <Accordion.Item>`);
  return ctx;
}

export interface AccordionRootProps extends React.ComponentPropsWithoutRef<"div"> {
  /** Items open on first render. Uncontrolled; omit for an all-closed start. */
  defaultValue?: string[];
  /** When false, opening one item closes the others. Defaults to true. */
  openMultiple?: boolean;
}

const AccordionRoot = React.forwardRef<HTMLDivElement, AccordionRootProps>(
  ({ className, defaultValue, openMultiple = true, children, ...props }, ref) => {
    const [openValues, setOpenValues] = React.useState<string[]>(defaultValue ?? []);
    const rootRef = React.useRef<HTMLDivElement>(null);
    React.useImperativeHandle(ref, () => rootRef.current as HTMLDivElement);

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

    const ctx = React.useMemo(() => ({ isOpen, toggle, rootRef }), [isOpen, toggle]);

    return (
      <AccordionRootContext.Provider value={ctx}>
        <div ref={rootRef} className={cn("flex flex-col", className)} {...props}>
          {children}
        </div>
      </AccordionRootContext.Provider>
    );
  },
);
AccordionRoot.displayName = "Accordion.Root";

export interface AccordionItemProps extends React.ComponentPropsWithoutRef<"div"> {
  /** Stable identity of this item, used as its open/closed key. */
  value: string;
}

const AccordionItem = React.forwardRef<HTMLDivElement, AccordionItemProps>(
  ({ className, value, children, ...props }, ref) => {
    const { isOpen } = useRootContext("Item");
    const id = React.useId();
    const ctx: AccordionItemContextValue = {
      value,
      open: isOpen(value),
      triggerId: `${id}-trigger`,
      panelId: `${id}-panel`,
    };
    return (
      <AccordionItemContext.Provider value={ctx}>
        <div ref={ref} className={cn("border-b border-line", className)} {...props}>
          {children}
        </div>
      </AccordionItemContext.Provider>
    );
  },
);
AccordionItem.displayName = "Accordion.Item";

const AccordionHeader = React.forwardRef<HTMLHeadingElement, React.ComponentPropsWithoutRef<"h3">>(
  ({ className, ...props }, ref) => (
    <h3 ref={ref} className={cn("flex", className)} {...props} />
  ),
);
AccordionHeader.displayName = "Accordion.Header";

const AccordionTrigger = React.forwardRef<HTMLButtonElement, React.ComponentPropsWithoutRef<"button">>(
  ({ className, onClick, onKeyDown, ...props }, ref) => {
    const { toggle, rootRef } = useRootContext("Trigger");
    const { value, open, triggerId, panelId } = useItemContext("Trigger");

    const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
      onKeyDown?.(event);
      if (event.defaultPrevented) return;
      const nav = ["ArrowDown", "ArrowUp", "Home", "End"];
      if (!nav.includes(event.key)) return;
      const root = rootRef.current;
      if (!root) return;
      event.preventDefault();
      const triggers = Array.from(
        root.querySelectorAll<HTMLButtonElement>("[data-accordion-trigger]"),
      );
      const i = triggers.indexOf(event.currentTarget);
      const target =
        event.key === "ArrowDown"
          ? triggers[(i + 1) % triggers.length]
          : event.key === "ArrowUp"
            ? triggers[(i - 1 + triggers.length) % triggers.length]
            : event.key === "Home"
              ? triggers[0]
              : triggers[triggers.length - 1];
      target?.focus();
    };

    return (
      <button
        ref={ref}
        type="button"
        id={triggerId}
        data-accordion-trigger=""
        aria-expanded={open}
        aria-controls={panelId}
        onClick={(event) => {
          onClick?.(event);
          if (!event.defaultPrevented) toggle(value);
        }}
        onKeyDown={handleKeyDown}
        className={cn(
          "flex flex-1 cursor-pointer items-center justify-between gap-md py-md text-left font-body text-base font-medium text-ink",
          focusRing,
          disabledStyles,
          className,
        )}
        {...props}
      />
    );
  },
);
AccordionTrigger.displayName = "Accordion.Trigger";

const AccordionPanel = React.forwardRef<HTMLDivElement, React.ComponentPropsWithoutRef<"div">>(
  ({ className, children, ...props }, ref) => {
    const { open, triggerId, panelId } = useItemContext("Panel");
    return (
      <div
        ref={ref}
        id={panelId}
        role="region"
        aria-labelledby={triggerId}
        inert={!open}
        className={cn(
          "grid transition-[grid-template-rows] duration-200 ease-out motion-reduce:transition-none",
          open ? "grid-rows-[1fr]" : "grid-rows-[0fr]",
        )}
        {...props}
      >
        <div className="overflow-hidden">
          <div className={cn("font-body text-sm text-muted", className)}>{children}</div>
        </div>
      </div>
    );
  },
);
AccordionPanel.displayName = "Accordion.Panel";

export const Accordion = {
  Root: AccordionRoot,
  Item: AccordionItem,
  Header: AccordionHeader,
  Trigger: AccordionTrigger,
  Panel: AccordionPanel,
};
