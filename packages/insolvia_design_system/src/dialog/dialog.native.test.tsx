// NATIVE-leaf tests — the `native` vitest project resolves './dialog' to
// dialog.native.tsx and renders it through react-native-web, the pair the app
// ships on web. react-native-web's Modal renders a portal into the document,
// so `screen` queries reach the modal content as usual.
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import { colors } from '@insolvia-ai/tokens';

import { rgb, setPrefersColorScheme } from '../../vitest.native.setup';
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
      </Dialog.Popup>
    </Dialog.Root>
  );
}

describe('Dialog (native leaf)', () => {
  it('mounts the modal content when the trigger is pressed', async () => {
    const user = userEvent.setup();
    render(<DeleteDraft />);

    expect(screen.queryByText('Delete this draft?')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Delete draft' }));

    // The name resolves only if the Modal's aria-labelledby reaches the
    // rendered Title's nativeID in the emitted DOM.
    expect(screen.getByRole('dialog', { name: 'Delete this draft?' })).toBeInTheDocument();
    expect(screen.getByText('Delete this draft?')).toBeInTheDocument();
    expect(screen.getByText('The draft filing is removed permanently.')).toBeInTheDocument();
  });

  it('closes from the Close part', async () => {
    const user = userEvent.setup();
    render(<DeleteDraft />);

    await user.click(screen.getByRole('button', { name: 'Delete draft' }));
    expect(screen.getByRole('dialog')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  // The 0.2.1 regression class: colors must resolve from the scheme at render
  // time, never from colors.light at module load. The dialog element itself is
  // react-native-web's Modal container (transparent by design), so the scheme
  // assertion reads the Title's ink color instead of a container background.
  it('resolves dark colors when the OS scheme is dark', async () => {
    setPrefersColorScheme('dark');
    const user = userEvent.setup();
    render(<DeleteDraft />);

    await user.click(screen.getByRole('button', { name: 'Delete draft' }));

    const title = screen.getByText('Delete this draft?');
    expect(rgb(title.style.color)).toEqual(rgb(colors.dark.ink));
  });
});
