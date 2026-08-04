// NATIVE-leaf tests. They run in the vitest `native` project, whose resolver
// is Metro's view of the package (native-first extensions, react-native
// aliased to react-native-web), so the extensionless './tabs' below lands on
// tabs.native.tsx and renders through the same react-native-web the app ships
// on web. Assertions are made on the DOM it emits.
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Text } from 'react-native';
import { describe, expect, it } from 'vitest';

import { colors } from '@insolvia-ai/tokens';

import { rgb, setPrefersColorScheme } from '../../vitest.native.setup';
import { Tabs } from './tabs';

function Settings() {
  return (
    <Tabs.Root defaultValue="cost">
      <Tabs.List>
        <Tabs.Tab value="cost">Cost</Tabs.Tab>
        <Tabs.Tab value="security">Security</Tabs.Tab>
      </Tabs.List>
      <Tabs.Panel value="cost">
        <Text>Flat per-seat pricing, billed annually.</Text>
      </Tabs.Panel>
      <Tabs.Panel value="security">
        <Text>SOC 2 Type II, audited yearly.</Text>
      </Tabs.Panel>
    </Tabs.Root>
  );
}

describe('Tabs (native leaf)', () => {
  it('renders as tabs and switches the active panel on press', async () => {
    const user = userEvent.setup();
    render(<Settings />);

    const costTab = screen.getByRole('tab', { name: 'Cost' });
    const securityTab = screen.getByRole('tab', { name: 'Security' });

    expect(screen.getByText('Flat per-seat pricing, billed annually.')).toBeVisible();
    expect(screen.queryByText('SOC 2 Type II, audited yearly.')).not.toBeInTheDocument();

    await user.click(securityTab);

    expect(screen.getByText('SOC 2 Type II, audited yearly.')).toBeVisible();
    expect(screen.queryByText('Flat per-seat pricing, billed annually.')).not.toBeInTheDocument();
    expect(costTab).toBeInTheDocument();
  });

  it('exposes accessibilityState.selected via aria-selected', async () => {
    const user = userEvent.setup();
    render(<Settings />);

    const costTab = screen.getByRole('tab', { name: 'Cost' });
    const securityTab = screen.getByRole('tab', { name: 'Security' });

    expect(costTab).toHaveAttribute('aria-selected', 'true');
    expect(securityTab).toHaveAttribute('aria-selected', 'false');

    await user.click(securityTab);

    expect(costTab).toHaveAttribute('aria-selected', 'false');
    expect(securityTab).toHaveAttribute('aria-selected', 'true');
  });

  // The 0.2.1 regression: every native leaf baked in `colors.light` at module
  // load, so a dark-mode app rendered light design-system surfaces. Colors
  // must resolve from the scheme at render time.
  it('resolves dark colors for the active tab label when the OS scheme is dark', () => {
    setPrefersColorScheme('dark');

    render(<Settings />);

    const costTab = screen.getByText('Cost');
    expect(rgb(costTab.style.color)).toEqual(rgb(colors.dark.ink));
  });

  it('resolves light colors for the active tab label when the OS scheme is light', () => {
    render(<Settings />);

    const costTab = screen.getByText('Cost');
    expect(rgb(costTab.style.color)).toEqual(rgb(colors.light.ink));
  });
});
