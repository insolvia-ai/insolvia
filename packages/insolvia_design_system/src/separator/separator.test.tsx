import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { Separator } from './separator';

describe('Separator', () => {
  it('defaults to a horizontal separator', () => {
    render(<Separator />);

    const separator = screen.getByRole('separator');
    expect(separator).toHaveAttribute('aria-orientation', 'horizontal');
  });

  it('renders a vertical separator when requested', () => {
    render(<Separator orientation="vertical" />);

    const separator = screen.getByRole('separator');
    expect(separator).toHaveAttribute('aria-orientation', 'vertical');
  });

  it('lets a caller-supplied className win over the base classes', () => {
    render(<Separator className="my-custom-line" />);

    expect(screen.getByRole('separator')).toHaveClass('my-custom-line');
  });
});
