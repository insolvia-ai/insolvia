import * as React from 'react';

import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { Checkbox } from '../checkbox/checkbox';
import { CheckboxGroup } from './checkbox-group';

function DebtTypes(props: React.ComponentProps<typeof CheckboxGroup.Root>) {
  return (
    <CheckboxGroup.Root aria-label="Debt types" {...props}>
      <Checkbox.Root aria-label="Credit card" value="credit-card" />
      <Checkbox.Root aria-label="Medical" value="medical" />
      <Checkbox.Root aria-label="Student loan" value="student-loan" />
    </CheckboxGroup.Root>
  );
}

describe('CheckboxGroup', () => {
  it('renders a role="group" wrapper', () => {
    render(<DebtTypes />);
    expect(screen.getByRole('group', { name: 'Debt types' })).toBeInTheDocument();
  });

  it('starts empty without a defaultValue and checks members on click', async () => {
    const user = userEvent.setup();
    render(<DebtTypes />);

    const creditCard = screen.getByRole('checkbox', { name: 'Credit card' });
    const medical = screen.getByRole('checkbox', { name: 'Medical' });
    expect(creditCard).toHaveAttribute('aria-checked', 'false');
    expect(medical).toHaveAttribute('aria-checked', 'false');

    await user.click(creditCard);

    expect(creditCard).toHaveAttribute('aria-checked', 'true');
    expect(medical).toHaveAttribute('aria-checked', 'false');
  });

  it('honours defaultValue and lets a later toggle remove a member', async () => {
    const user = userEvent.setup();
    render(<DebtTypes defaultValue={['credit-card', 'medical']} />);

    const creditCard = screen.getByRole('checkbox', { name: 'Credit card' });
    const medical = screen.getByRole('checkbox', { name: 'Medical' });
    expect(creditCard).toHaveAttribute('aria-checked', 'true');
    expect(medical).toHaveAttribute('aria-checked', 'true');

    await user.click(medical);

    expect(medical).toHaveAttribute('aria-checked', 'false');
    expect(creditCard).toHaveAttribute('aria-checked', 'true');
  });

  it('is controlled when given `value`: toggling reports the change but the caller must apply it', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(<DebtTypes value={['credit-card']} onValueChange={onValueChange} />);

    const studentLoan = screen.getByRole('checkbox', { name: 'Student loan' });
    await user.click(studentLoan);

    expect(onValueChange).toHaveBeenCalledWith(['credit-card', 'student-loan']);
    // The parent didn't re-render with a new `value`, so nothing changed yet.
    expect(studentLoan).toHaveAttribute('aria-checked', 'false');
  });

  it('disables every member checkbox when the group is disabled', () => {
    render(<DebtTypes disabled />);

    for (const name of ['Credit card', 'Medical', 'Student loan']) {
      expect(screen.getByRole('checkbox', { name })).toBeDisabled();
    }
  });
});
