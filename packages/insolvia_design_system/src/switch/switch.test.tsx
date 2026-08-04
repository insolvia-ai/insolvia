import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { Switch } from './switch';

describe('Switch', () => {
  it('renders off by default', () => {
    render(
      <Switch.Root>
        <Switch.Thumb />
      </Switch.Root>,
    );

    expect(screen.getByRole('switch')).toHaveAttribute('aria-checked', 'false');
  });

  it('honours defaultChecked', () => {
    render(
      <Switch.Root defaultChecked>
        <Switch.Thumb />
      </Switch.Root>,
    );

    expect(screen.getByRole('switch')).toHaveAttribute('aria-checked', 'true');
  });

  it('toggles on click and reports the change', async () => {
    const user = userEvent.setup();
    const onCheckedChange = vi.fn();

    render(
      <Switch.Root onCheckedChange={onCheckedChange}>
        <Switch.Thumb />
      </Switch.Root>,
    );

    const toggle = screen.getByRole('switch');
    await user.click(toggle);

    expect(toggle).toHaveAttribute('aria-checked', 'true');
    expect(onCheckedChange).toHaveBeenCalledWith(true);

    await user.click(toggle);

    expect(toggle).toHaveAttribute('aria-checked', 'false');
    expect(onCheckedChange).toHaveBeenLastCalledWith(false);
  });

  it('never toggles when disabled', async () => {
    const user = userEvent.setup();
    const onCheckedChange = vi.fn();

    render(
      <Switch.Root disabled onCheckedChange={onCheckedChange}>
        <Switch.Thumb />
      </Switch.Root>,
    );

    const toggle = screen.getByRole('switch');
    expect(toggle).toBeDisabled();

    await user.click(toggle);

    expect(onCheckedChange).not.toHaveBeenCalled();
    expect(toggle).toHaveAttribute('aria-checked', 'false');
  });

  it('reflects the checked state on the thumb via data-state', () => {
    render(
      <Switch.Root defaultChecked>
        <Switch.Thumb data-testid="thumb" />
      </Switch.Root>,
    );

    expect(screen.getByTestId('thumb')).toHaveAttribute('data-state', 'checked');
  });
});
