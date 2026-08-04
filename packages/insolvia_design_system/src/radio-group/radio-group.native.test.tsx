// NATIVE-leaf tests — see button.native.test.tsx for how the `native` vitest
// project resolves './radio-group' to radio-group.native.tsx and renders it
// through react-native-web, the pair the app ships on web. `aria-checked`/
// `aria-disabled` are asserted directly (not `accessibilityState`) because
// that is what actually reaches the DOM through react-native-web — see the
// header comment in radio-group.native.tsx for why.
import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { colors } from '@insolvia-ai/tokens';

import { rgb, setPrefersColorScheme } from '../../vitest.native.setup';
import { RadioGroup } from './radio-group';

describe('RadioGroup (native leaf)', () => {
  it('renders a radiogroup of radios with checked/disabled state wired', () => {
    render(
      <RadioGroup.Root defaultValue="cash">
        <RadioGroup.Item value="cash" />
        <RadioGroup.Item value="card" disabled />
      </RadioGroup.Root>,
    );

    expect(screen.getByRole('radiogroup')).toBeInTheDocument();
    const radios = screen.getAllByRole('radio');
    expect(radios).toHaveLength(2);
    expect(radios[0]).toHaveAttribute('aria-checked', 'true');
    expect(radios[1]).toHaveAttribute('aria-checked', 'false');
    expect(radios[1]).toHaveAttribute('aria-disabled', 'true');
  });

  it('selects an item on press and reports the change', async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();

    render(
      <RadioGroup.Root onValueChange={onValueChange}>
        <RadioGroup.Item value="cash" />
        <RadioGroup.Item value="card" />
      </RadioGroup.Root>,
    );

    const [, card] = screen.getAllByRole('radio');
    await user.click(card!);

    expect(card).toHaveAttribute('aria-checked', 'true');
    expect(onValueChange).toHaveBeenCalledWith('card');
  });

  it('never selects a disabled item on press', () => {
    // react-native-web renders `disabled` as `pointer-events: none` (a div,
    // not a real `<button disabled>`), which userEvent's pointer-events check
    // refuses to click outright — that refusal already proves disablement,
    // but it also stops us from proving RN's own press handling swallows the
    // press rather than just the browser's cursor styling. `fireEvent.click`
    // bypasses the pointer-events guard the same way collapsible's disabled
    // test does, so the assertion below is on the handler, not the guard.
    const onValueChange = vi.fn();

    render(
      <RadioGroup.Root onValueChange={onValueChange}>
        <RadioGroup.Item value="cash" disabled />
      </RadioGroup.Root>,
    );

    const radio = screen.getByRole('radio');
    fireEvent.click(radio);

    expect(onValueChange).not.toHaveBeenCalled();
    expect(radio).toHaveAttribute('aria-checked', 'false');
  });

  // The 0.2.1 regression: every native leaf baked in `colors.light` at module
  // load, so a dark-mode app rendered light design-system surfaces. Colors
  // must resolve from the scheme at render time.
  it('resolves dark colors for the checked item border when the OS scheme is dark', () => {
    setPrefersColorScheme('dark');

    render(
      <RadioGroup.Root defaultValue="cash">
        <RadioGroup.Item value="cash" />
      </RadioGroup.Root>,
    );

    // react-native-web serializes RN's single `borderColor` style into the
    // four CSS longhands (`border-top-color`, etc.), never the `border-color`
    // shorthand itself — jsdom's CSSStyleDeclaration does not synthesize the
    // shorthand getter from longhands it was never assigned through, so
    // `radio.style.borderColor` reads back empty. Any one of the four
    // longhands carries the same color; `borderTopColor` is the one every
    // other bordered native leaf in this package would need to check too.
    const radio = screen.getByRole('radio');
    expect(rgb(radio.style.borderTopColor)).toEqual(rgb(colors.dark.primary));
  });
});
