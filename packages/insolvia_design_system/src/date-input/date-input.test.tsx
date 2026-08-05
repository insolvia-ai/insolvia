import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import * as React from 'react';
import { describe, expect, it, vi } from 'vitest';

import { Field } from '../field';
import { DateInput } from './date-input';

describe('DateInput', () => {
  it('masks digits into YYYY-MM-DD as they are typed', async () => {
    const user = userEvent.setup();
    render(<DateInput aria-label="Date incurred" />);
    const input = screen.getByRole('textbox');
    await user.type(input, '20190214');
    expect(input).toHaveValue('2019-02-14');
  });

  it('reports only complete, real dates', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(<DateInput onValueChange={onValueChange} aria-label="Date incurred" />);
    await user.type(screen.getByRole('textbox'), '2019021');
    // Every keystroke so far is a partial date, so every call is ''.
    expect(onValueChange).toHaveBeenCalledTimes(7);
    expect(onValueChange.mock.calls.every(([v]) => v === '')).toBe(true);

    await user.type(screen.getByRole('textbox'), '4');
    expect(onValueChange).toHaveBeenLastCalledWith('2019-02-14');
  });

  it('does not mark a half-typed date invalid', async () => {
    const user = userEvent.setup();
    render(<DateInput aria-label="Date incurred" />);
    const input = screen.getByRole('textbox');
    await user.type(input, '2019');
    expect(input).not.toHaveAttribute('aria-invalid');
  });

  it('marks an impossible date invalid and reports nothing', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(<DateInput onValueChange={onValueChange} aria-label="Date incurred" />);
    const input = screen.getByRole('textbox');
    await user.type(input, '20190230');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(onValueChange).toHaveBeenLastCalledWith('');
  });

  it('accepts 29 February in a leap year', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(<DateInput onValueChange={onValueChange} aria-label="Date incurred" />);
    await user.type(screen.getByRole('textbox'), '20200229');
    expect(onValueChange).toHaveBeenLastCalledWith('2020-02-29');
    expect(screen.getByRole('textbox')).not.toHaveAttribute('aria-invalid');
  });

  it('marks a date outside min/max invalid', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(<DateInput max="2020-01-01" onValueChange={onValueChange} aria-label="Date incurred" />);
    const input = screen.getByRole('textbox');
    await user.type(input, '20200614');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(onValueChange).toHaveBeenLastCalledWith('');
  });

  it('removes exactly one digit per Backspace, separators included', async () => {
    const user = userEvent.setup();
    render(<DateInput aria-label="Date incurred" />);
    const input = screen.getByRole('textbox');
    await user.type(input, '201902');
    expect(input).toHaveValue('2019-02');
    // "2019-02" -> "2019-0" -> "2019" (deleting the '0' takes the now-dangling
    // separator with it) -> "201". No keystroke is swallowed by the mask.
    await user.type(input, '{Backspace}');
    expect(input).toHaveValue('2019-0');
    await user.type(input, '{Backspace}');
    expect(input).toHaveValue('2019');
    await user.type(input, '{Backspace}');
    expect(input).toHaveValue('201');
  });

  it('offers a numeric keypad and refuses autofill', () => {
    render(<DateInput aria-label="Date incurred" />);
    const input = screen.getByRole('textbox');
    expect(input).toHaveAttribute('inputmode', 'numeric');
    expect(input).toHaveAttribute('autocomplete', 'off');
  });

  it('shows the expected format as a placeholder', () => {
    render(<DateInput aria-label="Date incurred" />);
    expect(screen.getByRole('textbox')).toHaveAttribute('placeholder', 'YYYY-MM-DD');
  });

  describe('submission', () => {
    it('submits the ISO date under the field name', async () => {
      const user = userEvent.setup();
      const { container } = render(
        <Field.Root name="incurred">
          <Field.Label>Date incurred</Field.Label>
          <DateInput />
        </Field.Root>,
      );
      await user.type(screen.getByRole('textbox'), '20190214');
      expect(container.querySelector('input[type="hidden"]')).toHaveValue('2019-02-14');
    });

    it('submits nothing while the date is half-typed', async () => {
      const user = userEvent.setup();
      const { container } = render(
        <Field.Root name="incurred">
          <Field.Label>Date incurred</Field.Label>
          <DateInput />
        </Field.Root>,
      );
      await user.type(screen.getByRole('textbox'), '2019');
      // The visible input reads "2019"; what a form post would carry is empty.
      expect(container.querySelector('input[type="hidden"]')).toHaveValue('');
    });
  });

  describe('inside a Field', () => {
    it('is named by the field label', () => {
      render(
        <Field.Root>
          <Field.Label>Date incurred</Field.Label>
          <DateInput />
        </Field.Root>,
      );
      expect(screen.getByRole('textbox', { name: 'Date incurred' })).toBeTruthy();
    });

    it('takes the field description and invalid state', () => {
      render(
        <Field.Root invalid>
          <Field.Label>Date incurred</Field.Label>
          <DateInput />
          <Field.Error>Enter the date the debt was incurred.</Field.Error>
        </Field.Root>,
      );
      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('aria-invalid', 'true');
      expect(input).toHaveAccessibleDescription('Enter the date the debt was incurred.');
    });
  });

  it('renders the controlled value it is given', () => {
    render(<DateInput value="2019-02-14" aria-label="Date incurred" />);
    expect(screen.getByRole('textbox')).toHaveValue('2019-02-14');
  });

  it('cannot be typed into while disabled', async () => {
    const user = userEvent.setup();
    render(<DateInput disabled aria-label="Date incurred" />);
    const input = screen.getByRole('textbox');
    await user.type(input, '2019');
    expect(input).toHaveValue('');
  });
});
