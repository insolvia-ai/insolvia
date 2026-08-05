// NATIVE-leaf tests, run in the vitest `native` project — native-first
// resolution with react-native aliased to react-native-web, the exact pair the
// app ships on web. That makes these the tests that matter for this component:
// the app never renders the `.web` leaf, so its keyboard grammar would be
// unreachable in the product if it lived only there.
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { colors } from '@insolvia-ai/tokens';

import { rgb, setPrefersColorScheme } from '../../vitest.native.setup';
import { Field } from '../field';
import { Select } from './select';
import type { SelectOption } from './select.props';

const DISTRICTS: SelectOption[] = [
  { value: 'ak', label: 'Alaska' },
  { value: 'az', label: 'Arizona' },
  { value: 'ca', label: 'California', disabled: true },
  { value: 'nj', label: 'New Jersey' },
  { value: 'ny', label: 'New York' },
];

describe('Select (native leaf)', () => {
  it('emits a combobox with the collapsed state', () => {
    render(<Select options={DISTRICTS} aria-label="District" placeholder="Choose a district" />);
    const combobox = screen.getByRole('combobox', { name: 'District' });
    expect(combobox).toHaveAttribute('aria-expanded', 'false');
    expect(combobox).toHaveTextContent('Choose a district');
    expect(screen.queryByRole('listbox')).toBeNull();
  });

  it('opens on press and commits the pressed option', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(<Select options={DISTRICTS} onValueChange={onValueChange} aria-label="District" />);

    await user.click(screen.getByRole('combobox'));
    expect(screen.getByRole('listbox')).toBeTruthy();

    await user.click(screen.getByRole('option', { name: 'New York' }));
    expect(onValueChange).toHaveBeenCalledWith('ny');
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(screen.getByRole('combobox')).toHaveTextContent('New York');
  });

  it('points aria-controls at the listbox it actually renders', async () => {
    const user = userEvent.setup();
    render(<Select options={DISTRICTS} aria-label="District" />);
    await user.click(screen.getByRole('combobox'));
    const id = screen.getByRole('combobox').getAttribute('aria-controls');
    expect(id).toBeTruthy();
    expect(document.getElementById(id!)).toBe(screen.getByRole('listbox'));
  });

  describe('keyboard — the reason this leaf has key handlers at all', () => {
    it('opens with ArrowDown and commits with Enter', async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(<Select options={DISTRICTS} onValueChange={onValueChange} aria-label="District" />);
      await user.tab();
      await user.keyboard('{ArrowDown}');
      expect(screen.getByRole('listbox')).toBeTruthy();
      await user.keyboard('{ArrowDown}{Enter}');
      expect(onValueChange).toHaveBeenCalledWith('az');
    });

    it('skips a disabled option while arrowing', async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(<Select options={DISTRICTS} onValueChange={onValueChange} aria-label="District" />);
      await user.tab();
      await user.keyboard('{ArrowDown}{ArrowDown}{ArrowDown}{Enter}');
      expect(onValueChange).toHaveBeenCalledWith('nj');
    });

    it('closes on Escape without committing', async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(<Select options={DISTRICTS} onValueChange={onValueChange} aria-label="District" />);
      await user.tab();
      await user.keyboard('{ArrowDown}{ArrowDown}{Escape}');
      expect(screen.queryByRole('listbox')).toBeNull();
      expect(onValueChange).not.toHaveBeenCalled();
    });

    it('reaches a multi-word label by typing through the space', async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(<Select options={DISTRICTS} onValueChange={onValueChange} aria-label="District" />);
      await user.tab();
      await user.keyboard('{ArrowDown}');
      await user.keyboard('new j{Enter}');
      expect(onValueChange).toHaveBeenCalledWith('nj');
    });

    it('tracks the highlight in aria-activedescendant', async () => {
      const user = userEvent.setup();
      render(<Select options={DISTRICTS} aria-label="District" />);
      await user.tab();
      await user.keyboard('{ArrowDown}');
      const id = screen.getByRole('combobox').getAttribute('aria-activedescendant');
      expect(document.getElementById(id!)).toBe(screen.getByRole('option', { name: 'Alaska' }));
    });
  });

  describe('inside a Field', () => {
    it('is named by the field label through aria-labelledby', () => {
      // The native direction: the control points BACK at the label, which is
      // what react-native-web can express — Field's native leaf establishes it.
      render(
        <Field.Root>
          <Field.Label>Filing district</Field.Label>
          <Select options={DISTRICTS} />
        </Field.Root>,
      );
      expect(screen.getByRole('combobox', { name: 'Filing district' })).toBeTruthy();
    });

    it('takes the field invalid state and description', () => {
      render(
        <Field.Root invalid>
          <Field.Label>Filing district</Field.Label>
          <Select options={DISTRICTS} />
          <Field.Error>A filing district is required.</Field.Error>
        </Field.Root>,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).toHaveAttribute('aria-invalid', 'true');
      expect(combobox).toHaveAccessibleDescription('A filing district is required.');
    });
  });

  it('cannot be pressed while disabled', () => {
    render(<Select options={DISTRICTS} disabled aria-label="District" />);
    const combobox = screen.getByRole('combobox');
    expect(combobox).toHaveAttribute('aria-disabled', 'true');
    // react-native-web's own guarantee for a disabled Pressable, and the thing
    // user-event refuses to click through — asserted directly rather than by
    // attempting a click that the test library will not perform.
    expect(combobox).toHaveStyle({ pointerEvents: 'none' });
  });

  // The 0.2.1 regression: colors baked in at module load stayed light inside a
  // dark app. Both schemes are asserted on the trigger label.
  it('resolves dark colors when the OS scheme is dark', () => {
    setPrefersColorScheme('dark');
    render(<Select options={DISTRICTS} value="ny" aria-label="District" />);
    expect(rgb(screen.getByText('New York').style.color)).toEqual(rgb(colors.dark.ink));
  });

  it('resolves light colors when the OS scheme is light', () => {
    render(<Select options={DISTRICTS} value="ny" aria-label="District" />);
    expect(rgb(screen.getByText('New York').style.color)).toEqual(rgb(colors.light.ink));
  });
});
