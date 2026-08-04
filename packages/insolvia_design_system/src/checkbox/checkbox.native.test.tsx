// NATIVE-leaf tests — see button.native.test.tsx for how the `native` vitest
// project resolves './checkbox' to checkbox.native.tsx and renders it through
// react-native-web, the pair the app ships on web. These pin the role/state
// wiring (accessibilityRole="checkbox" + aria-checked incl. "mixed") and the
// dark-mode color-resolution regression the whole package tests for.
import { render, screen } from '@testing-library/react';
import userEvent, { PointerEventsCheckLevel } from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { colors } from '@insolvia-ai/tokens';

import { rgb, setPrefersColorScheme } from '../../vitest.native.setup';
import { CheckboxGroup } from '../checkbox-group/checkbox-group';
import { Checkbox } from './checkbox';

describe('Checkbox (native leaf)', () => {
  it('renders with role checkbox and toggles aria-checked on press', async () => {
    const user = userEvent.setup();
    const onCheckedChange = vi.fn();

    render(
      <Checkbox.Root aria-label="Filed for bankruptcy before" onCheckedChange={onCheckedChange} />,
    );

    const box = screen.getByRole('checkbox', { name: 'Filed for bankruptcy before' });
    expect(box).toHaveAttribute('aria-checked', 'false');

    await user.click(box);

    expect(onCheckedChange).toHaveBeenCalledWith(true);
    expect(box).toHaveAttribute('aria-checked', 'true');
  });

  it('reports aria-checked="mixed" when indeterminate', () => {
    render(<Checkbox.Root aria-label="Select all debts" indeterminate />);

    const box = screen.getByRole('checkbox', { name: 'Select all debts' });
    expect(box).toHaveAttribute('aria-checked', 'mixed');
  });

  it('a disabled checkbox ignores presses', async () => {
    // react-native-web renders `disabled` as `pointer-events: none` (a div,
    // not a real `<button disabled>`), which user-event's default pointer
    // check already refuses to click — that refusal IS proof of disablement,
    // but bypassing it here also proves RN's own `usePressEvents` swallows
    // the press, not just the browser's cursor styling.
    const user = userEvent.setup({ pointerEventsCheck: PointerEventsCheckLevel.Never });
    const onCheckedChange = vi.fn();

    render(<Checkbox.Root aria-label="Locked" disabled onCheckedChange={onCheckedChange} />);

    const box = screen.getByRole('checkbox', { name: 'Locked' });
    expect(box).toHaveAttribute('aria-disabled', 'true');

    await user.click(box);

    expect(onCheckedChange).not.toHaveBeenCalled();
  });

  it('inside a CheckboxGroup, a value-bearing checkbox toggles group membership', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();

    render(
      <CheckboxGroup.Root defaultValue={[]} onValueChange={onValueChange}>
        <Checkbox.Root aria-label="Debts" value="debts" />
      </CheckboxGroup.Root>,
    );

    const box = screen.getByRole('checkbox', { name: 'Debts' });
    await user.click(box);

    expect(onValueChange).toHaveBeenCalledWith(['debts']);
  });

  // The 0.2.1 regression: every native leaf baked in `colors.light` at module
  // load, so a dark-mode app rendered light design-system surfaces. Colors
  // must resolve from the scheme at render time.
  it('resolves dark colors for a checked box when the OS scheme is dark', () => {
    setPrefersColorScheme('dark');

    render(<Checkbox.Root aria-label="Dark mode check" defaultChecked />);

    const box = screen.getByRole('checkbox', { name: 'Dark mode check' });
    expect(rgb(box.style.backgroundColor)).toEqual(rgb(colors.dark.primary));
  });
});
