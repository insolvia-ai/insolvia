import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import { Dialog } from './dialog';

function DeleteDraft() {
  return (
    <Dialog.Root>
      <Dialog.Trigger>Delete draft</Dialog.Trigger>
      <Dialog.Backdrop />
      <Dialog.Popup>
        <Dialog.Title>Delete this draft?</Dialog.Title>
        <Dialog.Description>The draft filing is removed permanently.</Dialog.Description>
        <Dialog.Close>Cancel</Dialog.Close>
        <button type="button">Delete</button>
      </Dialog.Popup>
    </Dialog.Root>
  );
}

describe('Dialog', () => {
  it('renders nothing until the trigger opens it, with dialog role and aria-modal', async () => {
    const user = userEvent.setup();
    render(<DeleteDraft />);

    const trigger = screen.getByRole('button', { name: 'Delete draft' });
    expect(trigger).toHaveAttribute('aria-haspopup', 'dialog');
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    await user.click(trigger);

    const dialog = screen.getByRole('dialog');
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
  });

  it('is labelled by its rendered Title and described by its rendered Description', async () => {
    const user = userEvent.setup();
    render(<DeleteDraft />);
    await user.click(screen.getByRole('button', { name: 'Delete draft' }));

    const dialog = screen.getByRole('dialog');
    const title = screen.getByText('Delete this draft?');
    const description = screen.getByText('The draft filing is removed permanently.');

    // The attributes must point at ids that exist in the rendered DOM…
    expect(dialog).toHaveAttribute('aria-labelledby', title.id);
    expect(dialog).toHaveAttribute('aria-describedby', description.id);
    // …and the association must resolve to a real accessible name/description.
    expect(dialog).toHaveAccessibleName('Delete this draft?');
    expect(dialog).toHaveAccessibleDescription('The draft filing is removed permanently.');
  });

  it('moves focus into the popup on open and returns it to the trigger on close', async () => {
    const user = userEvent.setup();
    render(<DeleteDraft />);

    const trigger = screen.getByRole('button', { name: 'Delete draft' });
    await user.click(trigger);

    // The first focusable element in the popup is the Close button.
    expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus();

    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
  });

  it('traps Tab and Shift+Tab inside the popup', async () => {
    const user = userEvent.setup();
    render(<DeleteDraft />);
    await user.click(screen.getByRole('button', { name: 'Delete draft' }));

    const cancel = screen.getByRole('button', { name: 'Cancel' });
    const del = screen.getByRole('button', { name: 'Delete' });

    expect(cancel).toHaveFocus();
    await user.tab();
    expect(del).toHaveFocus();
    await user.tab(); // wraps from last back to first
    expect(cancel).toHaveFocus();
    await user.tab({ shift: true }); // wraps from first back to last
    expect(del).toHaveFocus();
  });

  it('closes on Escape', async () => {
    const user = userEvent.setup();
    render(<DeleteDraft />);
    await user.click(screen.getByRole('button', { name: 'Delete draft' }));
    expect(screen.getByRole('dialog')).toBeInTheDocument();

    await user.keyboard('{Escape}');

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Delete draft' })).toHaveFocus();
  });

  it('closes when the backdrop is clicked', async () => {
    const user = userEvent.setup();
    render(<DeleteDraft />);
    await user.click(screen.getByRole('button', { name: 'Delete draft' }));

    // The backdrop is a decorative aria-hidden scrim, so it is unreachable by
    // role/text queries by design; the data hook is its only stable handle.
    const backdrop = document.querySelector<HTMLElement>('[data-dialog-backdrop]');
    expect(backdrop).not.toBeNull();
    await user.click(backdrop as HTMLElement);

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('closes from the Close part', async () => {
    const user = userEvent.setup();
    render(<DeleteDraft />);
    await user.click(screen.getByRole('button', { name: 'Delete draft' }));

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('locks body scroll while open and restores it on close', async () => {
    const user = userEvent.setup();
    render(<DeleteDraft />);

    expect(document.body.style.overflow).toBe('');
    await user.click(screen.getByRole('button', { name: 'Delete draft' }));
    expect(document.body.style.overflow).toBe('hidden');

    await user.keyboard('{Escape}');
    expect(document.body.style.overflow).toBe('');
  });
});
