// WEB LEAF — plain React DOM + Tailwind. Wires label -> control -> description
// -> error with htmlFor / aria-describedby / aria-invalid. Shares the id scheme
// and describedby rule with the native leaf (field.props); the elements, the
// child-presence scan (keyed off THIS leaf's Description/Error), and the
// render-as-select/textarea escape hatch are web-only.
import * as React from 'react';

import { cn } from '../lib/cn';
import { focusRing } from '../lib/styles';
import {
  FieldContext,
  composeDescribedBy,
  useFieldContext,
  useFieldIds,
  type FieldContextValue,
  type FieldRootOwnProps,
} from './field.props';

export interface FieldRootProps
  extends Omit<React.ComponentPropsWithoutRef<'div'>, 'children'>, FieldRootOwnProps {
  children?: React.ReactNode;
}

const FieldRoot = React.forwardRef<HTMLDivElement, FieldRootProps>(
  ({ className, name, invalid = false, children, ...props }, ref) => {
    const ids = useFieldIds();

    // Which of description/error are present decides `aria-describedby`.
    let hasDescription = false;
    let hasError = false;
    React.Children.forEach(children, (child) => {
      if (!React.isValidElement(child)) return;
      if (child.type === FieldDescription) hasDescription = true;
      if (child.type === FieldError) hasError = true;
    });

    const ctx: FieldContextValue = {
      controlId: ids.controlId,
      describedBy: composeDescribedBy(ids, hasDescription, hasError),
      invalid,
      name,
      descriptionId: ids.descriptionId,
      errorId: ids.errorId,
    };

    return (
      <FieldContext.Provider value={ctx}>
        <div ref={ref} className={cn('flex flex-col gap-xs', className)} {...props}>
          {children}
        </div>
      </FieldContext.Provider>
    );
  },
);
FieldRoot.displayName = 'Field.Root';

const FieldLabel = React.forwardRef<HTMLLabelElement, React.ComponentPropsWithoutRef<'label'>>(
  ({ className, ...props }, ref) => {
    const { controlId } = useFieldContext('Label');
    return (
      <label
        ref={ref}
        htmlFor={controlId}
        className={cn('font-body text-sm font-medium text-ink', className)}
        {...props}
      />
    );
  },
);
FieldLabel.displayName = 'Field.Label';

const controlClass = cn(
  'h-10 w-full rounded-md border border-line bg-card px-sm font-body text-sm text-ink',
  'placeholder:text-muted',
  focusRing,
  'disabled:cursor-not-allowed disabled:bg-surface-alt disabled:text-muted',
  'aria-[invalid=true]:border-danger',
);

export interface FieldControlProps extends React.ComponentPropsWithoutRef<'input'> {
  /**
   * Render as a different element (a `<select>` or `<textarea>`) while still
   * receiving the field's id, name, and aria wiring. The element's own
   * `className` is merged after the control base.
   */
  render?: React.ReactElement<Record<string, unknown>>;
}

const FieldControl = React.forwardRef<HTMLInputElement, FieldControlProps>(
  ({ className, render, name: nameProp, ...props }, ref) => {
    const { controlId, describedBy, invalid, name } = useFieldContext('Control');
    const shared = {
      id: controlId,
      name: nameProp ?? name,
      'aria-describedby': describedBy,
      'aria-invalid': invalid ? true : undefined,
    } as const;

    if (render) {
      const childClassName = (render.props.className as string | undefined) ?? undefined;
      return React.cloneElement(render, {
        ...shared,
        ...props,
        ref,
        className: cn(controlClass, childClassName, className),
      });
    }

    return <input ref={ref} {...shared} className={cn(controlClass, className)} {...props} />;
  },
);
FieldControl.displayName = 'Field.Control';

const FieldDescription = React.forwardRef<
  HTMLParagraphElement,
  React.ComponentPropsWithoutRef<'p'>
>(({ className, ...props }, ref) => {
  const { descriptionId } = useFieldContext('Description');
  return (
    <p
      ref={ref}
      id={descriptionId}
      className={cn('font-body text-sm text-muted', className)}
      {...props}
    />
  );
});
FieldDescription.displayName = 'Field.Description';

export interface FieldErrorProps extends React.ComponentPropsWithoutRef<'p'> {
  /**
   * Accepted for call-site compatibility with the previous Base UI field, where
   * `match` gated the error on a validation state. Here the caller renders the
   * error conditionally, so this is a no-op.
   */
  match?: boolean;
}

const FieldError = React.forwardRef<HTMLParagraphElement, FieldErrorProps>(
  ({ className, match: _match, ...props }, ref) => {
    const { errorId } = useFieldContext('Error');
    return (
      <p
        ref={ref}
        id={errorId}
        className={cn('font-body text-sm text-danger', className)}
        {...props}
      />
    );
  },
);
FieldError.displayName = 'Field.Error';

export const Field = {
  Root: FieldRoot,
  Label: FieldLabel,
  Control: FieldControl,
  Description: FieldDescription,
  Error: FieldError,
};
