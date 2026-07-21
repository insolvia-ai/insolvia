import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { Footer } from './Footer';

function SiteFooter() {
  return (
    <Footer.Root>
      <Footer.Group title="Product">
        <Footer.Link href="/features">Features</Footer.Link>
        <Footer.Link href="/pricing">Pricing</Footer.Link>
      </Footer.Group>
      <Footer.Group title="Company">
        <Footer.Link href="/about">About</Footer.Link>
      </Footer.Group>
      <Footer.Note>© 2026 Insolvia</Footer.Note>
    </Footer.Root>
  );
}

describe('Footer', () => {
  it('renders a contentinfo landmark', () => {
    render(<SiteFooter />);

    expect(screen.getByRole('contentinfo')).toBeInTheDocument();
  });

  it('gives each link group its own named navigation landmark', () => {
    render(<SiteFooter />);

    expect(screen.getByRole('navigation', { name: 'Product' })).toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Company' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Product' })).toBeInTheDocument();
  });

  it('renders group links as list items pointing at their hrefs', () => {
    render(<SiteFooter />);

    expect(screen.getAllByRole('listitem')).toHaveLength(3);
    expect(screen.getByRole('link', { name: 'Pricing' })).toHaveAttribute('href', '/pricing');
    expect(screen.getByText('© 2026 Insolvia')).toBeInTheDocument();
  });
});
