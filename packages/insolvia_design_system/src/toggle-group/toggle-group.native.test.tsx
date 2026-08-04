// NATIVE-leaf tests — see button.native.test.tsx for how the `native` vitest
// project resolves './toggle-group' to toggle-group.native.tsx (and the
// sibling './toggle' to toggle.native.tsx) and renders them through
// react-native-web, the pair the app ships on web. Root itself carries no
// a11y wiring of its own (see its file header — there is no native
// `role="group"` equivalent, so it is a plain View), but it does carry real
// state wiring: the context every member `Toggle` reads its pressed value
// and `toggle()` from, and the `disabled` flag every member ORs with its
// own. That state surface is what this file pins, plus the dark-mode color
// regression check every native leaf in this package carries.
import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import { colors } from '@insolvia-ai/tokens';

import { rgb, setPrefersColorScheme } from '../../vitest.native.setup';
import { Toggle } from '../toggle';
import { ToggleGroup } from './toggle-group';

function AlignGroup({ multiple = false }: { multiple?: boolean }) {
  return (
    <ToggleGroup.Root defaultValue={['left']} multiple={multiple}>
      <Toggle value="left">Left</Toggle>
      <Toggle value="center">Center</Toggle>
    </ToggleGroup.Root>
  );
}

describe('ToggleGroup (native leaf)', () => {
  it('renders its member toggles and presses one on activation', async () => {
    const user = userEvent.setup();

    render(<AlignGroup />);

    const left = screen.getByRole('button', { name: 'Left' });
    const center = screen.getByRole('button', { name: 'Center' });
    expect(left).toHaveAttribute('aria-pressed', 'true');
    expect(center).toHaveAttribute('aria-pressed', 'false');

    await user.click(center);

    expect(center).toHaveAttribute('aria-pressed', 'true');
    // Single-select (multiple = false, the default): pressing one unpresses
    // the other — proof the group's context, not local toggle state, is
    // what changed.
    expect(left).toHaveAttribute('aria-pressed', 'false');
  });

  it('multi-select keeps both members pressed at once', async () => {
    const user = userEvent.setup();

    render(<AlignGroup multiple />);

    const left = screen.getByRole('button', { name: 'Left' });
    const center = screen.getByRole('button', { name: 'Center' });

    await user.click(center);

    expect(left).toHaveAttribute('aria-pressed', 'true');
    expect(center).toHaveAttribute('aria-pressed', 'true');
  });

  it('disabled on the Root disables every member toggle', () => {
    // react-native-web renders `disabled` as `pointer-events: none` (a div,
    // not a real `<button disabled>`), which userEvent's pointer-events check
    // refuses to click outright — that refusal already proves disablement,
    // but it also stops us from proving RN's own press handling swallows the
    // press rather than just the browser's cursor styling. `fireEvent.click`
    // bypasses the pointer-events guard the same way collapsible's disabled
    // test does, so the assertion below is on the handler, not the guard.
    render(
      <ToggleGroup.Root defaultValue={[]} disabled>
        <Toggle value="left">Left</Toggle>
      </ToggleGroup.Root>,
    );

    const left = screen.getByRole('button', { name: 'Left' });
    expect(left).toHaveAttribute('aria-disabled', 'true');

    fireEvent.click(left);

    expect(left).toHaveAttribute('aria-pressed', 'false');
  });

  // The 0.2.1 regression: every native leaf baked in `colors.light` at module
  // load, so a dark-mode app rendered light design-system surfaces. Colors
  // must resolve from the scheme at render time. ToggleGroup.Root itself has
  // no color (a bare View, see the file header); the color this asserts is
  // resolved by the member Toggle it renders, exercised through the group.
  it('resolves dark colors for a pressed member when the OS scheme is dark', () => {
    setPrefersColorScheme('dark');

    render(<AlignGroup />);

    const left = screen.getByRole('button', { name: 'Left' });
    expect(rgb(left.style.backgroundColor)).toEqual(rgb(colors.dark.primary));
  });
});
