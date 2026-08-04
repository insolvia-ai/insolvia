// WEB LEAF — plain React DOM + Tailwind, WAI-ARIA modal dialog. Shares the
// open/close machine and the title/description ids with the native leaf
// (dialog.props); what lives here is the DOM behavior: backdrop and popup
// portaled into document.body (react-dom is fine in a .web leaf — it is the
// web consumer's renderer), role="dialog"/aria-modal, a hand-rolled focus trap
// with focus-return to the opener, Escape + backdrop-click dismissal, and a
// body scroll lock. Closed means unmounted — nothing renders, no exit
// animation — so the popup's MOUNT effects are its open effects.
import * as React from 'react';
import { createPortal } from 'react-dom';

import { cn } from '../lib/cn';
import { disabledStyles, focusRing } from '../lib/styles';
import {
  DialogRootContext,
  useDialogRootContext,
  useDialogState,
  type DialogRootOwnProps,
} from './dialog.props';

// What the trap and the initial-focus walk consider tabbable. Covers
// everything a dialog card actually renders; a bespoke widget can always opt
// itself in with tabindex="0". The popup's own tabindex="-1" is deliberately
// excluded — it is a focus TARGET of last resort, not a tab stop.
const focusableSelector = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(', ');

/**
 * Keep Tab / Shift+Tab cycling inside the popup — a keydown walk over the
 * popup's focusable elements, no dependency needed. With nothing tabbable the
 * key is swallowed so focus stays parked on the popup itself.
 */
function trapTabKey(
  event: React.KeyboardEvent<HTMLDivElement>,
  popup: HTMLDivElement | null,
): void {
  if (!popup) return;
  const focusables = Array.from(popup.querySelectorAll<HTMLElement>(focusableSelector));
  const first = focusables[0];
  const last = focusables[focusables.length - 1];
  if (!first || !last) {
    event.preventDefault();
    return;
  }
  const active = document.activeElement;
  if (event.shiftKey && (active === first || active === popup)) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && (active === last || active === popup)) {
    event.preventDefault();
    first.focus();
  }
}

export interface DialogRootProps extends DialogRootOwnProps {
  children?: React.ReactNode;
}

// Renders no element of its own — it is the state owner and context provider.
const DialogRoot = ({ open, defaultOpen, onOpenChange, children }: DialogRootProps) => {
  const ctx = useDialogState(open, defaultOpen, onOpenChange);
  return <DialogRootContext.Provider value={ctx}>{children}</DialogRootContext.Provider>;
};

const DialogTrigger = React.forwardRef<
  HTMLButtonElement,
  React.ComponentPropsWithoutRef<'button'>
>(({ className, onClick, ...props }, ref) => {
  const { open, setOpen } = useDialogRootContext('Trigger');
  return (
    <button
      ref={ref}
      type="button"
      aria-haspopup="dialog"
      aria-expanded={open}
      onClick={(event) => {
        onClick?.(event);
        if (!event.defaultPrevented) setOpen(true);
      }}
      className={cn('cursor-pointer', focusRing, disabledStyles, className)}
      {...props}
    />
  );
});
DialogTrigger.displayName = 'Dialog.Trigger';

const DialogBackdrop = React.forwardRef<HTMLDivElement, React.ComponentPropsWithoutRef<'div'>>(
  ({ className, onClick, ...props }, ref) => {
    const { open, setOpen } = useDialogRootContext('Backdrop');
    if (!open) return null;
    return createPortal(
      <div
        ref={ref}
        data-dialog-backdrop=""
        // Decorative scrim: hidden from the accessibility tree; clicking it is
        // the pointer path to dismissal (Escape is the keyboard path).
        aria-hidden="true"
        onClick={(event) => {
          onClick?.(event);
          if (!event.defaultPrevented) setOpen(false);
        }}
        className={cn('fixed inset-0 z-40 bg-ink/50', className)}
        {...props}
      />,
      document.body,
    );
  },
);
DialogBackdrop.displayName = 'Dialog.Backdrop';

export type DialogPopupProps = React.ComponentPropsWithoutRef<'div'>;

// Mounted only while open, so its mount effects ARE the open effects: move
// focus in on open, return it to the opener on close, lock body scroll.
const DialogPopupImpl = React.forwardRef<HTMLDivElement, DialogPopupProps>(
  ({ className, children, onKeyDown, ...props }, ref) => {
    const { setOpen, titleId, descriptionId } = useDialogRootContext('Popup');
    const popupRef = React.useRef<HTMLDivElement | null>(null);
    const setRefs = React.useCallback(
      (node: HTMLDivElement | null) => {
        popupRef.current = node;
        if (typeof ref === 'function') ref(node);
        else if (ref) ref.current = node;
      },
      [ref],
    );

    // Same child-presence scan as Field.Root: aria-labelledby/-describedby
    // are composed from ONLY the parts actually rendered — a dangling
    // reference to an absent id is its own a11y defect. The scan compares
    // against THIS leaf's Title/Description identities.
    let hasTitle = false;
    let hasDescription = false;
    React.Children.forEach(children, (child) => {
      if (!React.isValidElement(child)) return;
      if (child.type === DialogTitle) hasTitle = true;
      if (child.type === DialogDescription) hasDescription = true;
    });

    // Focus in on open (first focusable, else the popup itself), back to the
    // opener on close. The opener is whatever held focus at mount — for a
    // trigger-opened dialog that is the trigger, with no ref plumbing needed.
    React.useEffect(() => {
      const popup = popupRef.current;
      if (!popup) return undefined;
      const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      (popup.querySelector<HTMLElement>(focusableSelector) ?? popup).focus();
      return () => opener?.focus();
    }, []);

    // Body scroll lock while open; restore whatever was there on close.
    React.useEffect(() => {
      const previous = document.body.style.overflow;
      document.body.style.overflow = 'hidden';
      return () => {
        document.body.style.overflow = previous;
      };
    }, []);

    const handleKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
      onKeyDown?.(event);
      if (event.defaultPrevented) return;
      if (event.key === 'Escape') {
        event.preventDefault();
        setOpen(false);
        return;
      }
      if (event.key === 'Tab') trapTabKey(event, popupRef.current);
    };

    return createPortal(
      <div
        ref={setRefs}
        role="dialog"
        aria-modal="true"
        aria-labelledby={hasTitle ? titleId : undefined}
        aria-describedby={hasDescription ? descriptionId : undefined}
        tabIndex={-1}
        onKeyDown={handleKeyDown}
        className={cn(
          'fixed left-1/2 top-1/2 z-50 flex w-full max-w-md -translate-x-1/2 -translate-y-1/2 flex-col gap-md rounded-lg bg-card p-lg shadow-lg outline-none',
          className,
        )}
        {...props}
      >
        {children}
      </div>,
      document.body,
    );
  },
);
DialogPopupImpl.displayName = 'Dialog.PopupImpl';

const DialogPopup = React.forwardRef<HTMLDivElement, DialogPopupProps>((props, ref) => {
  const { open } = useDialogRootContext('Popup');
  if (!open) return null;
  return <DialogPopupImpl ref={ref} {...props} />;
});
DialogPopup.displayName = 'Dialog.Popup';

const DialogTitle = React.forwardRef<HTMLHeadingElement, React.ComponentPropsWithoutRef<'h2'>>(
  ({ className, ...props }, ref) => {
    const { titleId } = useDialogRootContext('Title');
    return (
      <h2
        ref={ref}
        id={titleId}
        className={cn('font-heading text-lg font-semibold text-ink', className)}
        {...props}
      />
    );
  },
);
DialogTitle.displayName = 'Dialog.Title';

const DialogDescription = React.forwardRef<
  HTMLParagraphElement,
  React.ComponentPropsWithoutRef<'p'>
>(({ className, ...props }, ref) => {
  const { descriptionId } = useDialogRootContext('Description');
  return (
    <p
      ref={ref}
      id={descriptionId}
      className={cn('font-body text-sm text-muted', className)}
      {...props}
    />
  );
});
DialogDescription.displayName = 'Dialog.Description';

const DialogClose = React.forwardRef<HTMLButtonElement, React.ComponentPropsWithoutRef<'button'>>(
  ({ className, onClick, ...props }, ref) => {
    const { setOpen } = useDialogRootContext('Close');
    return (
      <button
        ref={ref}
        type="button"
        onClick={(event) => {
          onClick?.(event);
          if (!event.defaultPrevented) setOpen(false);
        }}
        className={cn('cursor-pointer', focusRing, disabledStyles, className)}
        {...props}
      />
    );
  },
);
DialogClose.displayName = 'Dialog.Close';

export const Dialog = {
  Root: DialogRoot,
  Trigger: DialogTrigger,
  Backdrop: DialogBackdrop,
  Popup: DialogPopup,
  Title: DialogTitle,
  Description: DialogDescription,
  Close: DialogClose,
};
