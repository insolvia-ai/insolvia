import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import { Toggle } from '../toggle';
import { ToggleGroup } from './toggle-group';

function AlignGroup({ multiple = false }: { multiple?: boolean }) {
  return (
    <ToggleGroup.Root defaultValue={['left']} multiple={multiple} aria-label="Text alignment">
      <Toggle value="left">Left</Toggle>
      <Toggle value="center">Center</Toggle>
      <Toggle value="right">Right</Toggle>
    </ToggleGroup.Root>
  );
}

describe('ToggleGroup', () => {
  it('renders a role="group" container', () => {
    render(<AlignGroup />);

    expect(screen.getByRole('group', { name: 'Text alignment' })).toBeInTheDocument();
  });

  it('single-select: pressing one toggle unpresses the previously pressed one', async () => {
    const user = userEvent.setup();
    render(<AlignGroup />);

    const left = screen.getByRole('button', { name: 'Left' });
    const center = screen.getByRole('button', { name: 'Center' });
    expect(left).toHaveAttribute('aria-pressed', 'true');

    await user.click(center);

    expect(center).toHaveAttribute('aria-pressed', 'true');
    expect(left).toHaveAttribute('aria-pressed', 'false');
  });

  it('multi-select: pressing another toggle leaves the first one pressed', async () => {
    const user = userEvent.setup();
    render(<AlignGroup multiple />);

    const left = screen.getByRole('button', { name: 'Left' });
    const center = screen.getByRole('button', { name: 'Center' });

    await user.click(center);

    expect(left).toHaveAttribute('aria-pressed', 'true');
    expect(center).toHaveAttribute('aria-pressed', 'true');
  });

  it('ArrowRight/ArrowLeft move focus between toggles, wrapping at the ends, without changing pressed state', async () => {
    const user = userEvent.setup();
    render(<AlignGroup />);

    const left = screen.getByRole('button', { name: 'Left' });
    const center = screen.getByRole('button', { name: 'Center' });
    const right = screen.getByRole('button', { name: 'Right' });

    left.focus();
    expect(left).toHaveFocus();

    await user.keyboard('{ArrowRight}');
    expect(center).toHaveFocus();

    await user.keyboard('{ArrowRight}');
    expect(right).toHaveFocus();

    // Wraps past the last item back to the first.
    await user.keyboard('{ArrowRight}');
    expect(left).toHaveFocus();

    // Wraps the other direction too.
    await user.keyboard('{ArrowLeft}');
    expect(right).toHaveFocus();

    // Focus movement never activates a toggle.
    expect(left).toHaveAttribute('aria-pressed', 'true');
    expect(center).toHaveAttribute('aria-pressed', 'false');
    expect(right).toHaveAttribute('aria-pressed', 'false');
  });

  it('disabled on the Root disables every member toggle that does not set its own', async () => {
    const user = userEvent.setup();
    render(
      <ToggleGroup.Root defaultValue={[]} disabled aria-label="Text alignment">
        <Toggle value="left">Left</Toggle>
        <Toggle value="center">Center</Toggle>
      </ToggleGroup.Root>,
    );

    const left = screen.getByRole('button', { name: 'Left' });
    expect(left).toBeDisabled();

    await user.click(left);
    expect(left).toHaveAttribute('aria-pressed', 'false');
  });
});
