// NATIVE-leaf tests — the `native` vitest project resolves './alert-dialog'
// to alert-dialog.native.tsx and renders it through react-native-web, the
// pair the app ships on web. react-native-web's Modal renders a portal into
// the document, so `screen` queries reach the modal content as usual.
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import { colors } from '@insolvia-ai/tokens';

import { rgb, setPrefersColorScheme } from '../../vitest.native.setup';
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
      </AlertDialog.Popup>
    </AlertDialog.Root>
  );
}

describe('AlertDialog (native leaf)', () => {
  it('mounts the modal content when the trigger is pressed', async () => {
    const user = userEvent.setup();
    render(<DiscardIntake />);

    expect(screen.queryByText('Discard this intake?')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Discard intake' }));

    expect(screen.getByRole('alertdialog')).toBeInTheDocument();
    expect(screen.getByText('Discard this intake?')).toBeInTheDocument();
    expect(screen.getByText('Unsaved answers are lost.')).toBeInTheDocument();
  });

  it('closes from the Close part', async () => {
    const user = userEvent.setup();
    render(<DiscardIntake />);

    await user.click(screen.getByRole('button', { name: 'Discard intake' }));
    expect(screen.getByRole('alertdialog')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Keep editing' }));

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
  });

  // The 0.2.1 regression class: colors must resolve from the scheme at render
  // time, never from colors.light at module load.
  it('resolves dark colors when the OS scheme is dark', async () => {
    setPrefersColorScheme('dark');
    const user = userEvent.setup();
    render(<DiscardIntake />);

    await user.click(screen.getByRole('button', { name: 'Discard intake' }));

    const card = screen.getByRole('alertdialog');
    expect(rgb(card.style.backgroundColor)).toEqual(rgb(colors.dark.card));
  });
});
