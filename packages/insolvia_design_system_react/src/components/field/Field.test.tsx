import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import { Field } from './Field';

describe('Field', () => {
  it('associates the label with the control so the input is reachable by name', () => {
    render(
      <Field.Root>
        <Field.Label>Work email</Field.Label>
        <Field.Control type="email" placeholder="you@firm.com" />
      </Field.Root>,
    );

    // getByLabelText only resolves if the label/control association is real.
    const input = screen.getByLabelText('Work email');
    expect(input).toHaveAttribute('type', 'email');
  });

  it('accepts typed input', async () => {
    const user = userEvent.setup();

    render(
      <Field.Root>
        <Field.Label>Work email</Field.Label>
        <Field.Control type="email" />
      </Field.Root>,
    );

    const input = screen.getByLabelText('Work email');
    await user.type(input, 'attorney@firm.com');

    expect(input).toHaveValue('attorney@firm.com');
  });

  it('links its description to the control via aria-describedby', () => {
    render(
      <Field.Root>
        <Field.Label>Work email</Field.Label>
        <Field.Control />
        <Field.Description>We only use this to send your invite.</Field.Description>
      </Field.Root>,
    );

    expect(screen.getByLabelText('Work email')).toHaveAccessibleDescription(
      'We only use this to send your invite.',
    );
  });
});
