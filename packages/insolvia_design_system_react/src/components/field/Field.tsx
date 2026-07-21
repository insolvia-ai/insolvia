import type { Field as FieldNamespace } from '@base-ui/react/field';

import * as React from 'react';
import { Field as BaseField } from '@base-ui/react/field';

import { cn } from '../../lib/cn';
import { focusRing } from '../../lib/styles';

// Base UI's Field wires label/control/description/error together with the right
// `id`/`aria-describedby`/`aria-invalid` plumbing, so the waitlist input is
// correctly labelled without any manual `htmlFor` bookkeeping at the call site.
const FieldRoot = React.forwardRef<HTMLDivElement, FieldNamespace.Root.Props>(
  ({ className, ...props }, ref) => (
    <BaseField.Root ref={ref} className={cn('flex flex-col gap-xs', className)} {...props} />
  ),
);
FieldRoot.displayName = 'Field.Root';

const FieldLabel = React.forwardRef<HTMLLabelElement, FieldNamespace.Label.Props>(
  ({ className, ...props }, ref) => (
    <BaseField.Label
      ref={ref}
      className={cn('font-body text-sm font-medium text-ink data-[disabled]:text-muted', className)}
      {...props}
    />
  ),
);
FieldLabel.displayName = 'Field.Label';

const FieldControl = React.forwardRef<HTMLInputElement, FieldNamespace.Control.Props>(
  ({ className, ...props }, ref) => (
    <BaseField.Control
      ref={ref}
      className={cn(
        'h-10 w-full rounded-md border border-line bg-card px-sm font-body text-sm text-ink',
        'placeholder:text-muted',
        focusRing,
        'data-[disabled]:cursor-not-allowed data-[disabled]:bg-surface-alt data-[disabled]:text-muted',
        'data-[invalid]:border-danger',
        className,
      )}
      {...props}
    />
  ),
);
FieldControl.displayName = 'Field.Control';

const FieldDescription = React.forwardRef<HTMLParagraphElement, FieldNamespace.Description.Props>(
  ({ className, ...props }, ref) => (
    <BaseField.Description
      ref={ref}
      className={cn('font-body text-sm text-muted', className)}
      {...props}
    />
  ),
);
FieldDescription.displayName = 'Field.Description';

const FieldError = React.forwardRef<HTMLParagraphElement, FieldNamespace.Error.Props>(
  ({ className, ...props }, ref) => (
    <BaseField.Error
      ref={ref}
      className={cn('font-body text-sm text-danger', className)}
      {...props}
    />
  ),
);
FieldError.displayName = 'Field.Error';

export const Field = {
  Root: FieldRoot,
  Label: FieldLabel,
  Control: FieldControl,
  Description: FieldDescription,
  Error: FieldError,
};
