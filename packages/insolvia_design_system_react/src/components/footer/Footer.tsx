import * as React from 'react';

import { cn } from '../../lib/cn';
import { focusRing } from '../../lib/styles';

// `<footer>` is a contentinfo landmark. The link groups inside are each their
// own named `<nav>` so assistive tech announces "Product" / "Company" rather
// than one undifferentiated pile of links.
const FooterRoot = React.forwardRef<HTMLElement, React.ComponentPropsWithoutRef<'footer'>>(
  ({ className, ...props }, ref) => (
    <footer
      ref={ref}
      className={cn(
        'flex w-full flex-col gap-lg border-t border-line bg-surface-alt px-lg py-xl text-ink',
        className,
      )}
      {...props}
    />
  ),
);
FooterRoot.displayName = 'Footer.Root';

export interface FooterGroupProps extends Omit<React.ComponentPropsWithoutRef<'nav'>, 'title'> {
  /** Heading for the group; also becomes the nav landmark's accessible name. */
  title: string;
}

const FooterGroup = React.forwardRef<HTMLElement, FooterGroupProps>(
  ({ className, title, children, ...props }, ref) => (
    <nav ref={ref} aria-label={title} className={cn('flex flex-col gap-sm', className)} {...props}>
      <h2 className="font-heading text-sm font-semibold text-ink">{title}</h2>
      <ul className="flex list-none flex-col gap-xs p-0">{children}</ul>
    </nav>
  ),
);
FooterGroup.displayName = 'Footer.Group';

const FooterLink = React.forwardRef<HTMLAnchorElement, React.ComponentPropsWithoutRef<'a'>>(
  ({ className, children, ...props }, ref) => (
    <li>
      <a
        ref={ref}
        className={cn(
          'rounded-sm font-body text-sm text-muted no-underline transition-colors hover:text-ink',
          focusRing,
          className,
        )}
        {...props}
      >
        {children}
      </a>
    </li>
  ),
);
FooterLink.displayName = 'Footer.Link';

const FooterNote = React.forwardRef<HTMLParagraphElement, React.ComponentPropsWithoutRef<'p'>>(
  ({ className, ...props }, ref) => (
    <p
      ref={ref}
      className={cn('border-t border-line pt-md font-body text-sm text-muted', className)}
      {...props}
    />
  ),
);
FooterNote.displayName = 'Footer.Note';

export const Footer = {
  Root: FooterRoot,
  Group: FooterGroup,
  Link: FooterLink,
  Note: FooterNote,
};
