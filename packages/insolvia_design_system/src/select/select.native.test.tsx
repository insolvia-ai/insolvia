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
  describe('stacking', () => {
    // Reported from a real browser, not found by this suite: the open list
    // rendered BEHIND the description text, the file button and the submit
    // button that followed the Select in the form. react-native-web gives every
    // View `position: relative`, so a form is a run of positioned siblings
    // painting in DOM order — and a z-index on the popup alone cannot lift it
    // past a sibling that comes after the whole Select.
    it('lifts the whole control above what follows it while open', async () => {
      const user = userEvent.setup();
      // `testID` rides the spread onto the root View, which is the element
      // whose stacking matters — the popup's own z-index cannot help it.
      render(<Select options={DISTRICTS} aria-label="District" testID="select-root" />);

      // Asserted on the CLASS, not `style.zIndex`: react-native-web compiles a
      // StyleSheet rule into an atomic class (`r-zIndex-…`) and sets no inline
      // style, so reading `.style` here reports nothing whether or not the fix
      // is present — which is exactly how a test can pass against the bug.
      const root = screen.getByTestId('select-root');
      expect(root.className).not.toMatch(/r-zIndex-/);

      await user.click(screen.getByRole('combobox'));
      expect(root.className).toMatch(/r-zIndex-/);
    });

    it('creates no stacking context once closed', async () => {
      // A closed Select must not shadow anything of its own accord.
      const user = userEvent.setup();
      render(<Select options={DISTRICTS} aria-label="District" testID="select-root" />);
      const root = screen.getByTestId('select-root');

      await user.click(screen.getByRole('combobox'));
      expect(root.className).toMatch(/r-zIndex-/);

      await user.keyboard('{Escape}');
      expect(root.className).not.toMatch(/r-zIndex-/);
    });
  });

  it('carries its accessible name on the combobox and not on a wrapper', async () => {
    // THE DEFECT THIS GUARDS. `aria-label` used to stay in `...props` and land
    // on the root View as well as the combobox, so one control answered to its
    // own name twice: React Native Testing Library reported two matches, and a
    // screen reader met a name on a node with no role to attach it to.
    //
    // Asserting `getByLabelText` throws on a duplicate is what catches it —
    // `getByRole` alone passes either way, because the combobox was always
    // labelled correctly.
    render(<Select aria-label="Filing district" options={DISTRICTS} />);

    // Exactly one node answers to the name. `getAllBy` rather than `getBy`
    // because `getBy` throwing on a duplicate would be a less legible failure
    // than a length assertion.
    expect(screen.getAllByLabelText('Filing district')).toHaveLength(1);
    expect(screen.getByRole('combobox', { name: 'Filing district' })).toBeTruthy();
  });

  it('names the open listbox after the control', async () => {
    // A listbox with no accessible name is announced as an unlabelled group.
    // The web leaf names its <ul>; this makes the native leaf agree.
    const user = userEvent.setup();
    render(<Select aria-label="Filing district" options={DISTRICTS} />);

    await user.click(screen.getByRole('combobox'));

    expect(screen.getByRole('listbox', { name: 'Filing district' })).toBeTruthy();
  });
});
