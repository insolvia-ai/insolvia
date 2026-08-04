// NATIVE-leaf tests — see button.native.test.tsx for how the `native` vitest
// project resolves './switch' to switch.native.tsx and renders it through
// react-native-web, the pair the app ships on web. `aria-checked`/
// `aria-disabled` are asserted directly (not `accessibilityState`) because
// that is what actually reaches the DOM through react-native-web — see the
// header comment in switch.native.tsx for why.
import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { colors } from '@insolvia-ai/tokens';

import { rgb, setPrefersColorScheme } from '../../vitest.native.setup';
import { Switch } from './switch';

describe('Switch (native leaf)', () => {
  it('renders as a switch and fires onCheckedChange on press', async () => {
    const user = userEvent.setup();
    const onCheckedChange = vi.fn();

    render(
      <Switch.Root onCheckedChange={onCheckedChange}>
        <Switch.Thumb />
      </Switch.Root>,
    );

    const toggle = screen.getByRole('switch');
    expect(toggle).toHaveAttribute('aria-checked', 'false');

    await user.click(toggle);

    expect(toggle).toHaveAttribute('aria-checked', 'true');
    expect(onCheckedChange).toHaveBeenCalledWith(true);
  });

  it('marks a disabled switch and never toggles it', () => {
    // react-native-web renders `disabled` as `pointer-events: none` (a div,
    // not a real `<button disabled>`), which userEvent's pointer-events check
    // refuses to click outright — that refusal already proves disablement,
    // but it also stops us from proving RN's own press handling swallows the
    // press rather than just the browser's cursor styling. `fireEvent.click`
    // bypasses the pointer-events guard the same way collapsible's disabled
    // test does, so the assertion below is on the handler, not the guard.
    const onCheckedChange = vi.fn();

    render(
      <Switch.Root disabled onCheckedChange={onCheckedChange}>
        <Switch.Thumb />
      </Switch.Root>,
    );

    const toggle = screen.getByRole('switch');
    expect(toggle).toHaveAttribute('aria-disabled', 'true');

    fireEvent.click(toggle);

    expect(onCheckedChange).not.toHaveBeenCalled();
    expect(toggle).toHaveAttribute('aria-checked', 'false');
  });

  // The 0.2.1 regression: every native leaf baked in `colors.light` at module
  // load, so a dark-mode app rendered light design-system surfaces. Colors
  // must resolve from the scheme at render time.
  it('resolves dark colors for the checked track when the OS scheme is dark', () => {
    setPrefersColorScheme('dark');

    render(
      <Switch.Root defaultChecked>
        <Switch.Thumb />
      </Switch.Root>,
    );

    const toggle = screen.getByRole('switch');
    expect(rgb(toggle.style.backgroundColor)).toEqual(rgb(colors.dark.primary));
  });
});
