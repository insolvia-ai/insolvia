import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { Card } from './Card';

describe('Card', () => {
  it('renders its title as a heading alongside body and footer content', () => {
    render(
      <Card.Root>
        <Card.Title>Chapter 7 in minutes</Card.Title>
        <Card.Body>Schedules, means test, and the petition, all from one intake.</Card.Body>
        <Card.Footer>
          <span>Included in every plan</span>
        </Card.Footer>
      </Card.Root>,
    );

    expect(screen.getByRole('heading', { name: 'Chapter 7 in minutes' })).toBeInTheDocument();
    expect(screen.getByText(/Schedules, means test/)).toBeInTheDocument();
    expect(screen.getByText('Included in every plan')).toBeInTheDocument();
  });

  it('applies the raised elevation variant', () => {
    render(
      <Card.Root elevation="raised" data-testid="card">
        <Card.Title>Raised</Card.Title>
      </Card.Root>,
    );

    expect(screen.getByTestId('card')).toHaveClass('shadow-md');
  });
});
