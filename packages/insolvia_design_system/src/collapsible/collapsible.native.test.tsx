// NATIVE-leaf tests — see button.native.test.tsx for how the `native` vitest
// project resolves './collapsible' to collapsible.native.tsx and renders it
// through react-native-web, the pair the app ships on web.
//
// No assertion on `aria-expanded` here: the pinned react-native-web (0.21.2)
// only maps `accessibilityState.disabled` to `aria-disabled`/`disabled` (see
// its AccessibilityUtil/isDisabled.js) — `expanded` is not translated to
// `aria-expanded` in this version, even though accordion.native.tsx sets the
// same `accessibilityState={{ expanded: open }}` prop. The Pressable still
// carries the semantically-correct RN prop for real native rendering; the
// open/closed state is instead verified the way it is actually observable in
// this DOM — via the panel mounting and unmounting.
import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import { colors } from '@insolvia-ai/tokens';

import { rgb, setPrefersColorScheme } from '../../vitest.native.setup';
import { Collapsible } from './collapsible';

function Notes() {
  return (
    <Collapsible.Root>
      <Collapsible.Trigger>Show case notes</Collapsible.Trigger>
      <Collapsible.Panel>Filed jointly, no prior filings.</Collapsible.Panel>
    </Collapsible.Root>
  );
}

describe('Collapsible (native leaf)', () => {
  it('renders the trigger as a button, unmounts the panel while closed, and mounts it on press', async () => {
    const user = userEvent.setup();

    render(<Notes />);

    const trigger = screen.getByRole('button', { name: 'Show case notes' });
    expect(screen.queryByText('Filed jointly, no prior filings.')).not.toBeInTheDocument();

    await user.click(trigger);

    expect(screen.getByText('Filed jointly, no prior filings.')).toBeVisible();

    await user.click(trigger);

    expect(screen.queryByText('Filed jointly, no prior filings.')).not.toBeInTheDocument();
  });

  it('reports disabled state and does not toggle when the root is disabled', () => {
    render(
      <Collapsible.Root disabled>
        <Collapsible.Trigger>Show case notes</Collapsible.Trigger>
        <Collapsible.Panel>Filed jointly, no prior filings.</Collapsible.Panel>
      </Collapsible.Root>,
    );

    const trigger = screen.getByRole('button', { name: 'Show case notes' });
    expect(trigger).toBeDisabled();
    expect(trigger).toHaveAttribute('aria-disabled', 'true');

    // A real disabled <button> does not dispatch click handlers at all (jsdom
    // mirrors browser behavior here); firing the event directly is enough to
    // prove the panel never mounts, without userEvent's pointer-events check.
    fireEvent.click(trigger);

    expect(screen.queryByText('Filed jointly, no prior filings.')).not.toBeInTheDocument();
  });

  // The 0.2.1 regression: every native leaf baked in `colors.light` at module
  // load, so a dark-mode app rendered light design-system surfaces. Colors
  // must resolve from the scheme at render time.
  it('resolves dark colors for the trigger label when the OS scheme is dark', () => {
    setPrefersColorScheme('dark');

    render(<Notes />);

    const label = screen.getByText('Show case notes');
    expect(rgb(label.style.color)).toEqual(rgb(colors.dark.ink));
  });
});
