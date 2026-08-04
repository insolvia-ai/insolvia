// WEB LEAF — plain React DOM + Tailwind, WAI-ARIA alert dialog. Shares the
// dialog's state machine and id scheme (via alert-dialog.props); the rendered
// markup deliberately DUPLICATES dialog.web.tsx rather than sharing a helper:
// the two leaves differ exactly where an alert dialog's contract lives —
// role="alertdialog", Escape does NOT close, the backdrop does NOT dismiss —
// and threading three behavior flags through a shared internal component
// would bury that contract in conditionals. Same trap, same portal, same
// scroll lock; dismissal only through an explicit choice (Close/action).
import * as React from 'react';
import { createPortal } from 'react-dom';

import { cn } from '../lib/cn';
import { disabledStyles, focusRing } from '../lib/styles';
import {
  AlertDialogRootContext,
  useAlertDialogRootContext,
  useAlertDialogState,
  type AlertDialogRootOwnProps,
} from './alert-dialog.props';

// Same tabbable-walk contract as the dialog leaf (see dialog.web.tsx).
const focusableSelector = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(', ');

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

export interface AlertDialogRootProps extends AlertDialogRootOwnProps {
  children?: React.ReactNode;
}

// Renders no element of its own — it is the state owner and context provider.
const AlertDialogRoot = ({ open, defaultOpen, onOpenChange, children }: AlertDialogRootProps) => {
  const ctx = useAlertDialogState(open, defaultOpen, onOpenChange);
  return <AlertDialogRootContext.Provider value={ctx}>{children}</AlertDialogRootContext.Provider>;
};

const AlertDialogTrigger = React.forwardRef<
  HTMLButtonElement,
  React.ComponentPropsWithoutRef<'button'>
>(({ className, onClick, ...props }, ref) => {
  const { open, setOpen } = useAlertDialogRootContext('Trigger');
  return (
    <button
      ref={ref}
      type="button"
      // "dialog" is the correct aria-haspopup token for an alertdialog too —
      // the attribute has no "alertdialog" value.
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
AlertDialogTrigger.displayName = 'AlertDialog.Trigger';

const AlertDialogBackdrop = React.forwardRef<
  HTMLDivElement,
  React.ComponentPropsWithoutRef<'div'>
>(({ className, ...props }, ref) => {
  const { open } = useAlertDialogRootContext('Backdrop');
  if (!open) return null;
  return createPortal(
    <div
      ref={ref}
      data-alert-dialog-backdrop=""
      // Decorative scrim only. Unlike the dialog's backdrop it has NO click
      // handler: an alert dialog requires an explicit choice to dismiss.
      aria-hidden="true"
      className={cn('fixed inset-0 z-40 bg-ink/50', className)}
      {...props}
    />,
    document.body,
  );
});
AlertDialogBackdrop.displayName = 'AlertDialog.Backdrop';

export type AlertDialogPopupProps = React.ComponentPropsWithoutRef<'div'>;

// Mounted only while open, so its mount effects ARE the open effects: move
// focus in on open, return it to the opener on close, lock body scroll.
const AlertDialogPopupImpl = React.forwardRef<HTMLDivElement, AlertDialogPopupProps>(
  ({ className, children, onKeyDown, ...props }, ref) => {
    const { titleId, descriptionId } = useAlertDialogRootContext('Popup');
    const popupRef = React.useRef<HTMLDivElement | null>(null);
    const setRefs = React.useCallback(
      (node: HTMLDivElement | null) => {
        popupRef.current = node;
        if (typeof ref === 'function') ref(node);
        else if (ref) ref.current = node;
      },
      [ref],
    );

    // Same child-presence scan as Field.Root — point only at rendered parts.
    let hasTitle = false;
    let hasDescription = false;
    React.Children.forEach(children, (child) => {
      if (!React.isValidElement(child)) return;
      if (child.type === AlertDialogTitle) hasTitle = true;
      if (child.type === AlertDialogDescription) hasDescription = true;
    });

    React.useEffect(() => {
      const popup = popupRef.current;
      if (!popup) return undefined;
      const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      (popup.querySelector<HTMLElement>(focusableSelector) ?? popup).focus();
      return () => opener?.focus();
    }, []);

    React.useEffect(() => {
      const previous = document.body.style.overflow;
      document.body.style.overflow = 'hidden';
      return () => {
        document.body.style.overflow = previous;
      };
    }, []);

    // No Escape branch: dismissal is an explicit choice. Tab still cycles.
    const handleKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
      onKeyDown?.(event);
      if (event.defaultPrevented) return;
      if (event.key === 'Tab') trapTabKey(event, popupRef.current);
    };

    return createPortal(
      <div
        ref={setRefs}
        role="alertdialog"
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
AlertDialogPopupImpl.displayName = 'AlertDialog.PopupImpl';

const AlertDialogPopup = React.forwardRef<HTMLDivElement, AlertDialogPopupProps>((props, ref) => {
  const { open } = useAlertDialogRootContext('Popup');
  if (!open) return null;
  return <AlertDialogPopupImpl ref={ref} {...props} />;
});
AlertDialogPopup.displayName = 'AlertDialog.Popup';

const AlertDialogTitle = React.forwardRef<
  HTMLHeadingElement,
  React.ComponentPropsWithoutRef<'h2'>
>(({ className, ...props }, ref) => {
  const { titleId } = useAlertDialogRootContext('Title');
  return (
    <h2
      ref={ref}
      id={titleId}
      className={cn('font-heading text-lg font-semibold text-ink', className)}
      {...props}
    />
  );
});
AlertDialogTitle.displayName = 'AlertDialog.Title';

const AlertDialogDescription = React.forwardRef<
  HTMLParagraphElement,
  React.ComponentPropsWithoutRef<'p'>
>(({ className, ...props }, ref) => {
  const { descriptionId } = useAlertDialogRootContext('Description');
  return (
    <p
      ref={ref}
      id={descriptionId}
      className={cn('font-body text-sm text-muted', className)}
      {...props}
    />
  );
});
AlertDialogDescription.displayName = 'AlertDialog.Description';

const AlertDialogClose = React.forwardRef<
  HTMLButtonElement,
  React.ComponentPropsWithoutRef<'button'>
>(({ className, onClick, ...props }, ref) => {
  const { setOpen } = useAlertDialogRootContext('Close');
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
});
AlertDialogClose.displayName = 'AlertDialog.Close';

export const AlertDialog = {
  Root: AlertDialogRoot,
  Trigger: AlertDialogTrigger,
  Backdrop: AlertDialogBackdrop,
  Popup: AlertDialogPopup,
  Title: AlertDialogTitle,
  Description: AlertDialogDescription,
  Close: AlertDialogClose,
};
