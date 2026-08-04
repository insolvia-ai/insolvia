import * as React from 'react';

import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { Collapsible } from './collapsible';

function Notes() {
  return (
    <Collapsible.Root>
      <Collapsible.Trigger>Show case notes</Collapsible.Trigger>
      <Collapsible.Panel>Filed jointly, no prior filings.</Collapsible.Panel>
    </Collapsible.Root>
  );
}

describe('Collapsible', () => {
  it('starts collapsed and opens the panel when the trigger is activated', async () => {
    const user = userEvent.setup();

    render(<Notes />);

    const trigger = screen.getByRole('button', { name: 'Show case notes' });
    expect(trigger).toHaveAttribute('aria-expanded', 'false');

    await user.click(trigger);

    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('Filed jointly, no prior filings.')).toBeVisible();
  });

  it('collapses again on a second activation', async () => {
    const user = userEvent.setup();

    render(<Notes />);

    const trigger = screen.getByRole('button', { name: 'Show case notes' });

    await user.click(trigger);
    expect(trigger).toHaveAttribute('aria-expanded', 'true');

    await user.click(trigger);
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
  });

  it('wires aria-controls on the trigger to the id-linked panel', () => {
    render(<Notes />);

    const trigger = screen.getByRole('button', { name: 'Show case notes' });
    const panel = screen.getByText('Filed jointly, no prior filings.').closest('[role="region"]');

    expect(panel).not.toBeNull();
    expect(trigger).toHaveAttribute('aria-controls', panel?.id);
    expect(panel).toHaveAttribute('aria-labelledby', trigger.id);
  });

  it('honours defaultOpen', () => {
    render(
      <Collapsible.Root defaultOpen>
        <Collapsible.Trigger>Show case notes</Collapsible.Trigger>
        <Collapsible.Panel>Filed jointly, no prior filings.</Collapsible.Panel>
      </Collapsible.Root>,
    );

    expect(screen.getByRole('button', { name: 'Show case notes' })).toHaveAttribute(
      'aria-expanded',
      'true',
    );
  });

  it('does not toggle when disabled', async () => {
    const user = userEvent.setup();

    render(
      <Collapsible.Root disabled>
        <Collapsible.Trigger>Show case notes</Collapsible.Trigger>
        <Collapsible.Panel>Filed jointly, no prior filings.</Collapsible.Panel>
      </Collapsible.Root>,
    );

    const trigger = screen.getByRole('button', { name: 'Show case notes' });
    expect(trigger).toBeDisabled();

    await user.click(trigger);

    expect(trigger).toHaveAttribute('aria-expanded', 'false');
  });

  it('supports controlled open state: the parent owns the value and toggling calls back', async () => {
    const user = userEvent.setup();
    const onOpenChange = vi.fn();

    function Controlled() {
      const [open, setOpen] = React.useState(false);
      return (
        <Collapsible.Root
          open={open}
          onOpenChange={(next) => {
            onOpenChange(next);
            setOpen(next);
          }}
        >
          <Collapsible.Trigger>Show case notes</Collapsible.Trigger>
          <Collapsible.Panel>Filed jointly, no prior filings.</Collapsible.Panel>
        </Collapsible.Root>
      );
    }

    render(<Controlled />);

    const trigger = screen.getByRole('button', { name: 'Show case notes' });
    expect(trigger).toHaveAttribute('aria-expanded', 'false');

    await user.click(trigger);

    expect(onOpenChange).toHaveBeenCalledWith(true);
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
  });

  it('in controlled mode, does not change state on its own when the parent declines to re-render', async () => {
    const user = userEvent.setup();
    const onOpenChange = vi.fn();

    render(
      <Collapsible.Root open={false} onOpenChange={onOpenChange}>
        <Collapsible.Trigger>Show case notes</Collapsible.Trigger>
        <Collapsible.Panel>Filed jointly, no prior filings.</Collapsible.Panel>
      </Collapsible.Root>,
    );

    const trigger = screen.getByRole('button', { name: 'Show case notes' });

    await user.click(trigger);

    expect(onOpenChange).toHaveBeenCalledWith(true);
    // The component asked to open via onOpenChange, but `open` is still
    // pinned to `false` by the parent, so the trigger must stay collapsed.
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
  });
});
