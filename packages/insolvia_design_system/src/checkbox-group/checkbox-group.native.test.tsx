// NATIVE-leaf tests — resolves './checkbox-group' to checkbox-group.native.tsx
// and './checkbox' to checkbox.native.tsx (both leaves), rendered through
// react-native-web the same way the app ships on web. Pins the role="group"
// wiring and that group membership actually reaches each checkbox's
// aria-checked state, not just its own props.
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import { Checkbox } from '../checkbox/checkbox';
import { CheckboxGroup } from './checkbox-group';

describe('CheckboxGroup (native leaf)', () => {
  it('renders a role="group" wrapper', () => {
    render(
      <CheckboxGroup.Root aria-label="Debt types">
        <Checkbox.Root aria-label="Credit card" value="credit-card" />
      </CheckboxGroup.Root>,
    );

    expect(screen.getByRole('group', { name: 'Debt types' })).toBeInTheDocument();
  });

  it('honours defaultValue and updates a member on press', async () => {
    const user = userEvent.setup();

    render(
      <CheckboxGroup.Root aria-label="Debt types" defaultValue={['credit-card']}>
        <Checkbox.Root aria-label="Credit card" value="credit-card" />
        <Checkbox.Root aria-label="Medical" value="medical" />
      </CheckboxGroup.Root>,
    );

    const creditCard = screen.getByRole('checkbox', { name: 'Credit card' });
    const medical = screen.getByRole('checkbox', { name: 'Medical' });
    expect(creditCard).toHaveAttribute('aria-checked', 'true');
    expect(medical).toHaveAttribute('aria-checked', 'false');

    await user.click(medical);

    expect(medical).toHaveAttribute('aria-checked', 'true');
  });

  it('disables every member checkbox when the group is disabled', () => {
    render(
      <CheckboxGroup.Root aria-label="Debt types" disabled>
        <Checkbox.Root aria-label="Credit card" value="credit-card" />
      </CheckboxGroup.Root>,
    );

    // react-native-web renders `disabled` as a div with `aria-disabled`, not
    // a real `<button disabled>` — see checkbox.native.test.tsx for the same
    // note on `toBeDisabled()` not applying here.
    expect(screen.getByRole('checkbox', { name: 'Credit card' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
  });
});
