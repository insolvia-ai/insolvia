import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { RadioGroup } from './radio-group';

function ThreeItems({ defaultValue }: { defaultValue?: string }) {
  return (
    <RadioGroup.Root defaultValue={defaultValue}>
      <RadioGroup.Item value="a">
        <RadioGroup.Indicator />
      </RadioGroup.Item>
      <RadioGroup.Item value="b">
        <RadioGroup.Indicator />
      </RadioGroup.Item>
      <RadioGroup.Item value="c">
        <RadioGroup.Indicator />
      </RadioGroup.Item>
    </RadioGroup.Root>
  );
}

describe('RadioGroup', () => {
  it('renders a radiogroup of radios, one checked from defaultValue', () => {
    render(<ThreeItems defaultValue="a" />);

    expect(screen.getByRole('radiogroup')).toBeInTheDocument();
    const radios = screen.getAllByRole('radio');
    expect(radios).toHaveLength(3);
    expect(radios[0]).toHaveAttribute('aria-checked', 'true');
    expect(radios[1]).toHaveAttribute('aria-checked', 'false');
    expect(radios[2]).toHaveAttribute('aria-checked', 'false');
  });

  it('selects an item on click and reports the change', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();

    render(
      <RadioGroup.Root onValueChange={onValueChange}>
        <RadioGroup.Item value="cash" />
        <RadioGroup.Item value="card" />
      </RadioGroup.Root>,
    );

    const [cash, card] = screen.getAllByRole('radio');
    await user.click(card!);

    expect(card).toHaveAttribute('aria-checked', 'true');
    expect(cash).toHaveAttribute('aria-checked', 'false');
    expect(onValueChange).toHaveBeenCalledWith('card');
  });

  it('moves selection with ArrowDown/ArrowRight and follows focus', async () => {
    const user = userEvent.setup();
    render(<ThreeItems defaultValue="a" />);

    const [a, b] = screen.getAllByRole('radio');
    a!.focus();
    await user.keyboard('{ArrowDown}');

    expect(b).toHaveAttribute('aria-checked', 'true');
    expect(b).toHaveFocus();
  });

  it('wraps to the last item on ArrowUp/ArrowLeft from the first item', async () => {
    const user = userEvent.setup();
    render(<ThreeItems defaultValue="a" />);

    const radios = screen.getAllByRole('radio');
    radios[0]!.focus();
    await user.keyboard('{ArrowLeft}');

    expect(radios[2]).toHaveAttribute('aria-checked', 'true');
    expect(radios[2]).toHaveFocus();
  });

  it('skips a disabled item when navigating with arrow keys', async () => {
    const user = userEvent.setup();
    render(
      <RadioGroup.Root defaultValue="a">
        <RadioGroup.Item value="a" />
        <RadioGroup.Item value="b" disabled />
        <RadioGroup.Item value="c" />
      </RadioGroup.Root>,
    );

    const [a, , c] = screen.getAllByRole('radio');
    a!.focus();
    await user.keyboard('{ArrowDown}');

    expect(c).toHaveAttribute('aria-checked', 'true');
    expect(c).toHaveFocus();
  });

  it('never selects a disabled item on click', async () => {
    const user = userEvent.setup();
    render(
      <RadioGroup.Root>
        <RadioGroup.Item value="cash" disabled />
      </RadioGroup.Root>,
    );

    const radio = screen.getByRole('radio');
    expect(radio).toBeDisabled();

    await user.click(radio);

    expect(radio).toHaveAttribute('aria-checked', 'false');
  });

  it('makes the first item the sole tab stop when nothing is selected', () => {
    render(<ThreeItems />);

    const radios = screen.getAllByRole('radio');
    expect(radios[0]).toHaveAttribute('tabindex', '0');
    expect(radios[1]).toHaveAttribute('tabindex', '-1');
    expect(radios[2]).toHaveAttribute('tabindex', '-1');
  });

  it('renders the Indicator only inside the selected item', () => {
    render(<ThreeItems defaultValue="b" />);

    const radios = screen.getAllByRole('radio');
    expect(radios[0]!.querySelector('[data-radio-indicator]')).toBeNull();
    expect(radios[1]!.querySelector('[data-radio-indicator]')).not.toBeNull();
    expect(radios[2]!.querySelector('[data-radio-indicator]')).toBeNull();
  });
});
