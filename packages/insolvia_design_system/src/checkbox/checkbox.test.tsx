import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { CheckboxGroup } from '../checkbox-group/checkbox-group';
import { Checkbox } from './checkbox';

describe('Checkbox', () => {
  it('starts unchecked and toggles checked on click', async () => {
    const user = userEvent.setup();
    render(
      <Checkbox.Root aria-label="Filed for bankruptcy before">
        <Checkbox.Indicator>✓</Checkbox.Indicator>
      </Checkbox.Root>,
    );

    const box = screen.getByRole('checkbox', { name: 'Filed for bankruptcy before' });
    expect(box).toHaveAttribute('aria-checked', 'false');
    expect(screen.queryByText('✓')).not.toBeInTheDocument();

    await user.click(box);

    expect(box).toHaveAttribute('aria-checked', 'true');
    expect(screen.getByText('✓')).toBeVisible();

    await user.click(box);
    expect(box).toHaveAttribute('aria-checked', 'false');
  });

  it('toggles via the keyboard (native button semantics, Space and Enter)', async () => {
    const user = userEvent.setup();
    render(<Checkbox.Root aria-label="Has co-debtor" />);

    const box = screen.getByRole('checkbox', { name: 'Has co-debtor' });
    await user.tab();
    expect(box).toHaveFocus();

    await user.keyboard(' ');
    expect(box).toHaveAttribute('aria-checked', 'true');

    await user.keyboard('{Enter}');
    expect(box).toHaveAttribute('aria-checked', 'false');
  });

  it('reports aria-checked="mixed" when indeterminate, and shows the indicator', () => {
    render(
      <Checkbox.Root aria-label="Select all debts" indeterminate>
        <Checkbox.Indicator>—</Checkbox.Indicator>
      </Checkbox.Root>,
    );

    const box = screen.getByRole('checkbox', { name: 'Select all debts' });
    expect(box).toHaveAttribute('aria-checked', 'mixed');
    expect(screen.getByText('—')).toBeVisible();
  });

  it('a disabled checkbox ignores clicks', async () => {
    const user = userEvent.setup();
    const onCheckedChange = vi.fn();
    render(
      <Checkbox.Root aria-label="Locked" disabled onCheckedChange={onCheckedChange} />,
    );

    const box = screen.getByRole('checkbox', { name: 'Locked' });
    expect(box).toBeDisabled();

    await user.click(box);

    expect(onCheckedChange).not.toHaveBeenCalled();
    expect(box).toHaveAttribute('aria-checked', 'false');
  });

  it('is controlled when given `checked`: clicking reports the change but the caller must apply it', async () => {
    const user = userEvent.setup();
    const onCheckedChange = vi.fn();
    render(<Checkbox.Root aria-label="Controlled" checked={true} onCheckedChange={onCheckedChange} />);

    const box = screen.getByRole('checkbox', { name: 'Controlled' });
    expect(box).toHaveAttribute('aria-checked', 'true');

    await user.click(box);

    expect(onCheckedChange).toHaveBeenCalledWith(false);
    // The parent didn't re-render with a new `checked`, so it stays as given.
    expect(box).toHaveAttribute('aria-checked', 'true');
  });

  it('inside a CheckboxGroup, a value-bearing checkbox toggles group membership', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(
      <CheckboxGroup.Root defaultValue={['debts']} onValueChange={onValueChange}>
        <Checkbox.Root aria-label="Debts" value="debts" />
        <Checkbox.Root aria-label="Income" value="income" />
      </CheckboxGroup.Root>,
    );

    const debts = screen.getByRole('checkbox', { name: 'Debts' });
    const income = screen.getByRole('checkbox', { name: 'Income' });
    expect(debts).toHaveAttribute('aria-checked', 'true');
    expect(income).toHaveAttribute('aria-checked', 'false');

    await user.click(income);

    expect(onValueChange).toHaveBeenCalledWith(['debts', 'income']);
  });
});
