import * as React from 'react';

import { cn } from '../../lib/cn';

export type CardElevation = 'flat' | 'raised';

export interface CardProps extends React.ComponentPropsWithoutRef<'div'> {
  elevation?: CardElevation;
}

// Base UI has no card primitive — a card is pure surface, no behaviour — so
// this is a plain element styled straight off the semantic tokens.
const elevationStyles: Record<CardElevation, string> = {
  flat: 'shadow-none',
  raised: 'shadow-md',
};

const CardRoot = React.forwardRef<HTMLDivElement, CardProps>(
  ({ className, elevation = 'flat', ...props }, ref) => (
    <div
      ref={ref}
      className={cn(
        'flex flex-col gap-sm rounded-lg border border-line bg-card p-lg text-ink',
        elevationStyles[elevation],
        className,
      )}
      {...props}
    />
  ),
);
CardRoot.displayName = 'Card.Root';

const CardTitle = React.forwardRef<HTMLHeadingElement, React.ComponentPropsWithoutRef<'h3'>>(
  // `children` is threaded explicitly rather than left to the spread so
  // jsx-a11y can see the heading has content.
  ({ className, children, ...props }, ref) => (
    <h3
      ref={ref}
      className={cn('font-heading text-lg font-semibold text-ink', className)}
      {...props}
    >
      {children}
    </h3>
  ),
);
CardTitle.displayName = 'Card.Title';

const CardBody = React.forwardRef<HTMLParagraphElement, React.ComponentPropsWithoutRef<'p'>>(
  ({ className, ...props }, ref) => (
    <p ref={ref} className={cn('font-body text-sm text-muted', className)} {...props} />
  ),
);
CardBody.displayName = 'Card.Body';

const CardFooter = React.forwardRef<HTMLDivElement, React.ComponentPropsWithoutRef<'div'>>(
  ({ className, ...props }, ref) => (
    <div ref={ref} className={cn('flex items-center gap-sm pt-sm', className)} {...props} />
  ),
);
CardFooter.displayName = 'Card.Footer';

export const Card = {
  Root: CardRoot,
  Title: CardTitle,
  Body: CardBody,
  Footer: CardFooter,
};
