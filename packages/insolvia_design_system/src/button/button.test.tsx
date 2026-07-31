import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { Button } from './button';

describe('Button', () => {
  it('renders its label and fires onClick', async () => {
    const user = userEvent.setup();
    const onClick = vi.fn();

    render(<Button onClick={onClick}>Join the waitlist</Button>);

    const button = screen.getByRole('button', { name: 'Join the waitlist' });
    await user.click(button);

    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('does not fire onClick when disabled', async () => {
    const user = userEvent.setup();
    const onClick = vi.fn();

    render(
      <Button disabled onClick={onClick}>
        Join the waitlist
      </Button>,
    );

    // The Base UI predecessor asserted `data-disabled`; this leaf is a plain
    // <button>, so the native `disabled` attribute is the contract.
    const button = screen.getByRole('button', { name: 'Join the waitlist' });
    expect(button).toBeDisabled();

    await user.click(button);
    expect(onClick).not.toHaveBeenCalled();
  });

  it('applies the intent and size variant classes', () => {
    render(
      <Button intent="secondary" size="lg">
        Book a demo
      </Button>,
    );

    const button = screen.getByRole('button', { name: 'Book a demo' });
    expect(button).toHaveClass('bg-surface-alt');
    expect(button).toHaveClass('h-12');
  });
});
