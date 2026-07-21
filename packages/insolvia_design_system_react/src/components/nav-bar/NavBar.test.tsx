import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { NavBar } from './NavBar';

function Nav() {
  return (
    <NavBar.Root>
      <NavBar.Brand href="/">Insolvia</NavBar.Brand>
      <NavBar.Links>
        <NavBar.Link href="/features" active>
          Features
        </NavBar.Link>
        <NavBar.Link href="/pricing">Pricing</NavBar.Link>
      </NavBar.Links>
    </NavBar.Root>
  );
}

describe('NavBar', () => {
  it('renders a named navigation landmark', () => {
    render(<Nav />);

    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
  });

  it('renders its links inside a list', () => {
    render(<Nav />);

    const items = screen.getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(screen.getByRole('link', { name: 'Pricing' })).toHaveAttribute('href', '/pricing');
  });

  it('marks the active link with aria-current', () => {
    render(<Nav />);

    expect(screen.getByRole('link', { name: 'Features' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: 'Pricing' })).not.toHaveAttribute('aria-current');
  });
});
