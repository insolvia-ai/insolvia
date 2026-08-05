import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import * as React from 'react';
import { describe, expect, it, vi } from 'vitest';

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

const open = async (user: ReturnType<typeof userEvent.setup>) => {
  await user.click(screen.getByRole('combobox'));
  return screen.getByRole('listbox');
};

describe('Select', () => {
  it('shows the placeholder until something is chosen', () => {
    render(<Select options={DISTRICTS} placeholder="Choose a district" aria-label="District" />);
    expect(screen.getByRole('combobox')).toHaveTextContent('Choose a district');
  });

  it('renders the selected option label, not its value', () => {
    render(<Select options={DISTRICTS} value="nj" aria-label="District" />);
    expect(screen.getByRole('combobox')).toHaveTextContent('New Jersey');
  });

  it('exposes the collapsed combobox state before it is opened', () => {
    render(<Select options={DISTRICTS} aria-label="District" />);
    const combobox = screen.getByRole('combobox');
    expect(combobox).toHaveAttribute('aria-expanded', 'false');
    expect(combobox).toHaveAttribute('aria-haspopup', 'listbox');
    expect(screen.queryByRole('listbox')).toBeNull();
  });

  it('opens on click and lists every option', async () => {
    const user = userEvent.setup();
    render(<Select options={DISTRICTS} aria-label="District" />);
    await open(user);
    expect(screen.getAllByRole('option')).toHaveLength(DISTRICTS.length);
    expect(screen.getByRole('combobox')).toHaveAttribute('aria-expanded', 'true');
  });

  it('commits the clicked option and closes', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(<Select options={DISTRICTS} onValueChange={onValueChange} aria-label="District" />);
    await open(user);
    await user.click(screen.getByRole('option', { name: 'New York' }));
    expect(onValueChange).toHaveBeenCalledWith('ny');
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(screen.getByRole('combobox')).toHaveTextContent('New York');
  });

  it('marks only the committed option aria-selected', async () => {
    const user = userEvent.setup();
    render(<Select options={DISTRICTS} value="nj" aria-label="District" />);
    await open(user);
    expect(screen.getByRole('option', { name: 'New Jersey' })).toHaveAttribute(
      'aria-selected',
      'true',
    );
    expect(screen.getByRole('option', { name: 'New York' })).toHaveAttribute(
      'aria-selected',
      'false',
    );
  });

  it('will not commit a disabled option', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(<Select options={DISTRICTS} onValueChange={onValueChange} aria-label="District" />);
    await open(user);
    await user.click(screen.getByRole('option', { name: 'California' }));
    expect(onValueChange).not.toHaveBeenCalled();
    expect(screen.getByRole('listbox')).toBeTruthy();
  });

  describe('keyboard', () => {
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
      // ak -> az -> (California disabled) -> nj
      await user.keyboard('{ArrowDown}{ArrowDown}{ArrowDown}{Enter}');
      expect(onValueChange).toHaveBeenCalledWith('nj');
    });

    it('points aria-activedescendant at an option that actually exists', async () => {
      const user = userEvent.setup();
      render(<Select options={DISTRICTS} aria-label="District" />);
      await user.tab();
      await user.keyboard('{ArrowDown}');
      const id = screen.getByRole('combobox').getAttribute('aria-activedescendant');
      expect(id).toBeTruthy();
      expect(document.getElementById(id!)).toBe(screen.getByRole('option', { name: 'Alaska' }));
    });

    it('drops aria-activedescendant when closed, so it never dangles', async () => {
      const user = userEvent.setup();
      render(<Select options={DISTRICTS} aria-label="District" />);
      await user.tab();
      await user.keyboard('{ArrowDown}{Escape}');
      expect(screen.getByRole('combobox')).not.toHaveAttribute('aria-activedescendant');
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

    it('jumps to the ends with Home and End', async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(<Select options={DISTRICTS} onValueChange={onValueChange} aria-label="District" />);
      await user.tab();
      await user.keyboard('{ArrowDown}{End}{Enter}');
      expect(onValueChange).toHaveBeenCalledWith('ny');
    });

    it('reaches a multi-word label by typing through the space', async () => {
      // The space in "New Jersey" must extend the typeahead buffer rather than
      // committing whatever is highlighted — the regression this guards.
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(<Select options={DISTRICTS} onValueChange={onValueChange} aria-label="District" />);
      await user.tab();
      await user.keyboard('{ArrowDown}');
      await user.keyboard('new j');
      await user.keyboard('{Enter}');
      expect(onValueChange).toHaveBeenCalledWith('nj');
    });

    it('commits directly when typing at a closed combobox', async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(<Select options={DISTRICTS} onValueChange={onValueChange} aria-label="District" />);
      await user.tab();
      await user.keyboard('ari');
      expect(onValueChange).toHaveBeenCalledWith('az');
      expect(screen.queryByRole('listbox')).toBeNull();
    });
  });

  describe('inside a Field', () => {
    it('is named by the field label', () => {
      render(
        <Field.Root>
          <Field.Label>Filing district</Field.Label>
          <Select options={DISTRICTS} />
        </Field.Root>,
      );
      expect(screen.getByRole('combobox', { name: 'Filing district' })).toBeTruthy();
    });

    it('takes the field description and invalid state', () => {
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

    it('submits under the field name without being asked twice', () => {
      const { container } = render(
        <Field.Root name="district">
          <Field.Label>Filing district</Field.Label>
          <Select options={DISTRICTS} value="ny" />
        </Field.Root>,
      );
      const hidden = container.querySelector('input[type="hidden"]');
      expect(hidden).toHaveAttribute('name', 'district');
      expect(hidden).toHaveValue('ny');
    });
  });

  it('renders no hidden input when there is nothing to submit', () => {
    const { container } = render(<Select options={DISTRICTS} aria-label="District" />);
    expect(container.querySelector('input[type="hidden"]')).toBeNull();
  });

  it('cannot be opened while disabled', async () => {
    const user = userEvent.setup();
    render(<Select options={DISTRICTS} disabled aria-label="District" />);
    await user.click(screen.getByRole('combobox'));
    expect(screen.queryByRole('listbox')).toBeNull();
  });
});
