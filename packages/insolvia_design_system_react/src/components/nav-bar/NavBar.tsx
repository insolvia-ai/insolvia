import * as React from 'react';

import { cn } from '../../lib/cn';
import { focusRing } from '../../lib/styles';

// A landmark `<nav>` with an accessible name, so screen-reader users can jump
// straight to it and tell it apart from the footer's own nav.
const NavBarRoot = React.forwardRef<HTMLElement, React.ComponentPropsWithoutRef<'nav'>>(
  ({ className, 'aria-label': ariaLabel = 'Main', ...props }, ref) => (
    <nav
      ref={ref}
      aria-label={ariaLabel}
      className={cn(
        'flex w-full items-center justify-between gap-lg border-b border-line bg-bg px-lg py-md',
        className,
      )}
      {...props}
    />
  ),
);
NavBarRoot.displayName = 'NavBar.Root';

const NavBarBrand = React.forwardRef<HTMLAnchorElement, React.ComponentPropsWithoutRef<'a'>>(
  // `children` is threaded explicitly rather than left to the spread so
  // jsx-a11y can see the anchor has content.
  ({ className, href = '/', children, ...props }, ref) => (
    <a
      ref={ref}
      href={href}
      className={cn(
        'rounded-sm font-heading text-lg font-semibold text-brand no-underline',
        focusRing,
        className,
      )}
      {...props}
    >
      {children}
    </a>
  ),
);
NavBarBrand.displayName = 'NavBar.Brand';

const NavBarLinks = React.forwardRef<HTMLUListElement, React.ComponentPropsWithoutRef<'ul'>>(
  ({ className, ...props }, ref) => (
    <ul ref={ref} className={cn('flex list-none items-center gap-lg p-0', className)} {...props} />
  ),
);
NavBarLinks.displayName = 'NavBar.Links';

export interface NavBarLinkProps extends React.ComponentPropsWithoutRef<'a'> {
  /** Marks the link as the current page for both styling and assistive tech. */
  active?: boolean;
}

const NavBarLink = React.forwardRef<HTMLAnchorElement, NavBarLinkProps>(
  ({ className, active = false, children, ...props }, ref) => (
    <li>
      <a
        ref={ref}
        aria-current={active ? 'page' : undefined}
        className={cn(
          'rounded-sm font-body text-sm no-underline transition-colors hover:text-ink',
          active ? 'font-medium text-ink' : 'text-muted',
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
NavBarLink.displayName = 'NavBar.Link';

const NavBarActions = React.forwardRef<HTMLDivElement, React.ComponentPropsWithoutRef<'div'>>(
  ({ className, ...props }, ref) => (
    <div ref={ref} className={cn('flex items-center gap-sm', className)} {...props} />
  ),
);
NavBarActions.displayName = 'NavBar.Actions';

export const NavBar = {
  Root: NavBarRoot,
  Brand: NavBarBrand,
  Links: NavBarLinks,
  Link: NavBarLink,
  Actions: NavBarActions,
};
