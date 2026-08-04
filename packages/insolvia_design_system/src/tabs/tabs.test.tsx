import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import { Tabs } from './tabs';

function Settings() {
  return (
    <Tabs.Root defaultValue="cost">
      <Tabs.List>
        <Tabs.Tab value="cost">Cost</Tabs.Tab>
        <Tabs.Tab value="security">Security</Tabs.Tab>
        <Tabs.Tab value="support">Support</Tabs.Tab>
      </Tabs.List>
      <Tabs.Panel value="cost">Flat per-seat pricing, billed annually.</Tabs.Panel>
      <Tabs.Panel value="security">SOC 2 Type II, audited yearly.</Tabs.Panel>
      <Tabs.Panel value="support">Email support, next business day.</Tabs.Panel>
    </Tabs.Root>
  );
}

describe('Tabs', () => {
  it('renders the correct roles', () => {
    render(<Settings />);

    expect(screen.getByRole('tablist')).toBeInTheDocument();
    expect(screen.getAllByRole('tab')).toHaveLength(3);
    expect(screen.getByRole('tabpanel')).toBeInTheDocument();
  });

  it('starts on defaultValue and only renders the active panel', () => {
    render(<Settings />);

    expect(screen.getByText('Flat per-seat pricing, billed annually.')).toBeVisible();
    expect(screen.queryByText('SOC 2 Type II, audited yearly.')).not.toBeInTheDocument();
  });

  it('wires aria-selected, aria-controls, and aria-labelledby correctly', () => {
    render(<Settings />);

    const costTab = screen.getByRole('tab', { name: 'Cost' });
    const securityTab = screen.getByRole('tab', { name: 'Security' });
    const panel = screen.getByRole('tabpanel');

    expect(costTab).toHaveAttribute('aria-selected', 'true');
    expect(securityTab).toHaveAttribute('aria-selected', 'false');
    expect(costTab).toHaveAttribute('aria-controls', panel.id);
    expect(panel).toHaveAttribute('aria-labelledby', costTab.id);
  });

  it('only the active tab is in the tab order', () => {
    render(<Settings />);

    expect(screen.getByRole('tab', { name: 'Cost' })).toHaveAttribute('tabIndex', '0');
    expect(screen.getByRole('tab', { name: 'Security' })).toHaveAttribute('tabIndex', '-1');
    expect(screen.getByRole('tab', { name: 'Support' })).toHaveAttribute('tabIndex', '-1');
  });

  it('clicking a tab switches the active panel', async () => {
    const user = userEvent.setup();
    render(<Settings />);

    await user.click(screen.getByRole('tab', { name: 'Security' }));

    expect(screen.getByRole('tab', { name: 'Security' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByText('SOC 2 Type II, audited yearly.')).toBeVisible();
    expect(screen.queryByText('Flat per-seat pricing, billed annually.')).not.toBeInTheDocument();
  });

  it('ArrowRight moves focus to and activates the next tab, wrapping past the end', async () => {
    const user = userEvent.setup();
    render(<Settings />);

    const costTab = screen.getByRole('tab', { name: 'Cost' });
    const securityTab = screen.getByRole('tab', { name: 'Security' });
    const supportTab = screen.getByRole('tab', { name: 'Support' });

    costTab.focus();
    await user.keyboard('{ArrowRight}');
    expect(securityTab).toHaveFocus();
    expect(securityTab).toHaveAttribute('aria-selected', 'true');

    await user.keyboard('{ArrowRight}');
    expect(supportTab).toHaveFocus();
    expect(supportTab).toHaveAttribute('aria-selected', 'true');

    await user.keyboard('{ArrowRight}');
    expect(costTab).toHaveFocus();
    expect(costTab).toHaveAttribute('aria-selected', 'true');
  });

  it('ArrowLeft moves focus to and activates the previous tab, wrapping past the start', async () => {
    const user = userEvent.setup();
    render(<Settings />);

    const costTab = screen.getByRole('tab', { name: 'Cost' });
    const supportTab = screen.getByRole('tab', { name: 'Support' });

    costTab.focus();
    await user.keyboard('{ArrowLeft}');

    expect(supportTab).toHaveFocus();
    expect(supportTab).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByText('Email support, next business day.')).toBeVisible();
  });

  it('Home jumps to and activates the first tab, End jumps to and activates the last', async () => {
    const user = userEvent.setup();
    render(<Settings />);

    const costTab = screen.getByRole('tab', { name: 'Cost' });
    const securityTab = screen.getByRole('tab', { name: 'Security' });
    const supportTab = screen.getByRole('tab', { name: 'Support' });

    securityTab.focus();
    await user.keyboard('{End}');
    expect(supportTab).toHaveFocus();
    expect(supportTab).toHaveAttribute('aria-selected', 'true');

    await user.keyboard('{Home}');
    expect(costTab).toHaveFocus();
    expect(costTab).toHaveAttribute('aria-selected', 'true');
  });
});
