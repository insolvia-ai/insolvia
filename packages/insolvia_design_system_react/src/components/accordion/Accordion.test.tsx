import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import { Accordion } from './Accordion';

function Faq() {
  return (
    <Accordion.Root>
      <Accordion.Item value="cost">
        <Accordion.Header>
          <Accordion.Trigger>What does Insolvia cost?</Accordion.Trigger>
        </Accordion.Header>
        <Accordion.Panel>Flat per-seat pricing, billed annually.</Accordion.Panel>
      </Accordion.Item>
    </Accordion.Root>
  );
}

describe('Accordion', () => {
  it('starts collapsed and opens the panel when the trigger is activated', async () => {
    const user = userEvent.setup();

    render(<Faq />);

    const trigger = screen.getByRole('button', { name: 'What does Insolvia cost?' });
    expect(trigger).toHaveAttribute('aria-expanded', 'false');

    await user.click(trigger);

    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('Flat per-seat pricing, billed annually.')).toBeVisible();
  });

  it('collapses again on a second activation', async () => {
    const user = userEvent.setup();

    render(<Faq />);

    const trigger = screen.getByRole('button', { name: 'What does Insolvia cost?' });

    await user.click(trigger);
    expect(trigger).toHaveAttribute('aria-expanded', 'true');

    await user.click(trigger);
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
  });
});
