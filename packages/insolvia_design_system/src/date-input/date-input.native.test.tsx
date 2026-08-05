// NATIVE-leaf tests, run in the vitest `native` project — react-native aliased
// to react-native-web, the pair the app ships on web. This is the leaf the app
// actually renders, so the masking and validity behaviour is asserted here too
// rather than assumed to carry over from the shared module.
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { colors } from '@insolvia-ai/tokens';

import { rgb, setPrefersColorScheme } from '../../vitest.native.setup';
import { Field } from '../field';
import { DateInput } from './date-input';

describe('DateInput (native leaf)', () => {
  it('masks digits into YYYY-MM-DD as they are typed', async () => {
    const user = userEvent.setup();
    render(<DateInput aria-label="Date incurred" />);
    const input = screen.getByRole('textbox');
    await user.type(input, '20190214');
    expect(input).toHaveValue('2019-02-14');
  });

  it('reports only a complete, real date', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(<DateInput onValueChange={onValueChange} aria-label="Date incurred" />);
    await user.type(screen.getByRole('textbox'), '2019021');
    expect(onValueChange).toHaveBeenLastCalledWith('');
    await user.type(screen.getByRole('textbox'), '4');
    expect(onValueChange).toHaveBeenLastCalledWith('2019-02-14');
  });

  it('marks an impossible date invalid', async () => {
    const user = userEvent.setup();
    render(<DateInput aria-label="Date incurred" />);
    const input = screen.getByRole('textbox');
    await user.type(input, '20190230');
    expect(input).toHaveAttribute('aria-invalid', 'true');
  });

  it('leaves a half-typed date alone', async () => {
    const user = userEvent.setup();
    render(<DateInput aria-label="Date incurred" />);
    const input = screen.getByRole('textbox');
    await user.type(input, '2019');
    expect(input).not.toHaveAttribute('aria-invalid');
  });

  it('honours max', async () => {
    const user = userEvent.setup();
    render(<DateInput max="2020-01-01" aria-label="Date incurred" />);
    const input = screen.getByRole('textbox');
    await user.type(input, '20200614');
    expect(input).toHaveAttribute('aria-invalid', 'true');
  });

  describe('inside a Field', () => {
    it('is named by the field label through aria-labelledby', () => {
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

  it('cannot be typed into while disabled', async () => {
    const user = userEvent.setup();
    render(<DateInput disabled aria-label="Date incurred" />);
    const input = screen.getByRole('textbox');
    await user.type(input, '2019');
    expect(input).toHaveValue('');
  });

  // The 0.2.1 regression: colors baked in at module load stayed light inside a
  // dark app.
  it('resolves dark colors when the OS scheme is dark', () => {
    setPrefersColorScheme('dark');
    render(<DateInput value="2019-02-14" aria-label="Date incurred" />);
    expect(rgb(screen.getByRole('textbox').style.color)).toEqual(rgb(colors.dark.ink));
  });

  it('resolves light colors when the OS scheme is light', () => {
    render(<DateInput value="2019-02-14" aria-label="Date incurred" />);
    expect(rgb(screen.getByRole('textbox').style.color)).toEqual(rgb(colors.light.ink));
  });
});
