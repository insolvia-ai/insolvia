import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import { AlertDialog } from './alert-dialog';

function DiscardIntake() {
  return (
    <AlertDialog.Root>
      <AlertDialog.Trigger>Discard intake</AlertDialog.Trigger>
      <AlertDialog.Backdrop />
      <AlertDialog.Popup>
        <AlertDialog.Title>Discard this intake?</AlertDialog.Title>
        <AlertDialog.Description>Unsaved answers are lost.</AlertDialog.Description>
        <AlertDialog.Close>Keep editing</AlertDialog.Close>
        <button type="button">Discard</button>
      </AlertDialog.Popup>
    </AlertDialog.Root>
  );
}

describe('AlertDialog', () => {
  it('renders nothing until the trigger opens it, with alertdialog role and aria-modal', async () => {
    const user = userEvent.setup();
    render(<DiscardIntake />);

    const trigger = screen.getByRole('button', { name: 'Discard intake' });
    expect(trigger).toHaveAttribute('aria-haspopup', 'dialog');
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();

    await user.click(trigger);

    const dialog = screen.getByRole('alertdialog');
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
  });

  it('is labelled by its rendered Title and described by its rendered Description', async () => {
    const user = userEvent.setup();
    render(<DiscardIntake />);
    await user.click(screen.getByRole('button', { name: 'Discard intake' }));

    const dialog = screen.getByRole('alertdialog');
    const title = screen.getByText('Discard this intake?');
    const description = screen.getByText('Unsaved answers are lost.');

    expect(dialog).toHaveAttribute('aria-labelledby', title.id);
    expect(dialog).toHaveAttribute('aria-describedby', description.id);
    expect(dialog).toHaveAccessibleName('Discard this intake?');
    expect(dialog).toHaveAccessibleDescription('Unsaved answers are lost.');
  });

  it('moves focus into the popup on open and returns it to the trigger on close', async () => {
    const user = userEvent.setup();
    render(<DiscardIntake />);

    const trigger = screen.getByRole('button', { name: 'Discard intake' });
    await user.click(trigger);

    expect(screen.getByRole('button', { name: 'Keep editing' })).toHaveFocus();

    await user.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
  });

  // The alert-dialog contract: dismissal only through an explicit choice.
  it('does NOT close on Escape', async () => {
    const user = userEvent.setup();
    render(<DiscardIntake />);
    await user.click(screen.getByRole('button', { name: 'Discard intake' }));

    await user.keyboard('{Escape}');

    expect(screen.getByRole('alertdialog')).toBeInTheDocument();
  });

  it('does NOT close when the backdrop is clicked', async () => {
    const user = userEvent.setup();
    render(<DiscardIntake />);
    await user.click(screen.getByRole('button', { name: 'Discard intake' }));

    const backdrop = document.querySelector<HTMLElement>('[data-alert-dialog-backdrop]');
    expect(backdrop).not.toBeNull();
    await user.click(backdrop as HTMLElement);

    expect(screen.getByRole('alertdialog')).toBeInTheDocument();
  });

  it('closes from the Close part', async () => {
    const user = userEvent.setup();
    render(<DiscardIntake />);
    await user.click(screen.getByRole('button', { name: 'Discard intake' }));

    await user.click(screen.getByRole('button', { name: 'Keep editing' }));

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
  });

  it('traps Tab inside the popup', async () => {
    const user = userEvent.setup();
    render(<DiscardIntake />);
    await user.click(screen.getByRole('button', { name: 'Discard intake' }));

    const keep = screen.getByRole('button', { name: 'Keep editing' });
    const discard = screen.getByRole('button', { name: 'Discard' });

    expect(keep).toHaveFocus();
    await user.tab();
    expect(discard).toHaveFocus();
    await user.tab(); // wraps from last back to first
    expect(keep).toHaveFocus();
  });
});
