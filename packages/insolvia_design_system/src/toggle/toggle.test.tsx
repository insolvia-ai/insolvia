import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { Toggle } from './toggle';

describe('Toggle', () => {
  it('starts unpressed and flips aria-pressed on activation', async () => {
    const user = userEvent.setup();

    render(<Toggle>Bold</Toggle>);

    const toggle = screen.getByRole('button', { name: 'Bold' });
    expect(toggle).toHaveAttribute('aria-pressed', 'false');

    await user.click(toggle);
    expect(toggle).toHaveAttribute('aria-pressed', 'true');

    await user.click(toggle);
    expect(toggle).toHaveAttribute('aria-pressed', 'false');
  });

  it('honours defaultPressed', () => {
    render(<Toggle defaultPressed>Bold</Toggle>);

    expect(screen.getByRole('button', { name: 'Bold' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('is controlled when pressed is supplied: clicking does not change the DOM until the prop changes', async () => {
    const user = userEvent.setup();
    const onPressedChange = vi.fn();

    const { rerender } = render(
      <Toggle pressed={false} onPressedChange={onPressedChange}>
        Bold
      </Toggle>,
    );

    const toggle = screen.getByRole('button', { name: 'Bold' });
    await user.click(toggle);

    expect(onPressedChange).toHaveBeenCalledWith(true);
    expect(toggle).toHaveAttribute('aria-pressed', 'false');

    rerender(
      <Toggle pressed={true} onPressedChange={onPressedChange}>
        Bold
      </Toggle>,
    );
    expect(toggle).toHaveAttribute('aria-pressed', 'true');
  });

  it('does not activate or fire onPressedChange when disabled', async () => {
    const user = userEvent.setup();
    const onPressedChange = vi.fn();

    render(
      <Toggle disabled onPressedChange={onPressedChange}>
        Bold
      </Toggle>,
    );

    const toggle = screen.getByRole('button', { name: 'Bold' });
    expect(toggle).toBeDisabled();

    await user.click(toggle);

    expect(onPressedChange).not.toHaveBeenCalled();
    expect(toggle).toHaveAttribute('aria-pressed', 'false');
  });
});
