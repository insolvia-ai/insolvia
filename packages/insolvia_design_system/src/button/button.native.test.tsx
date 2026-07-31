// NATIVE-leaf tests. They run in the vitest `native` project, whose resolver
// is Metro's view of the package (native-first extensions, react-native
// aliased to react-native-web), so the extensionless './button' below lands on
// button.native.tsx and renders through the same react-native-web the app
// ships on web. Assertions are made on the DOM it emits.
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { colors } from '@insolvia-ai/tokens';

import { rgb, setPrefersColorScheme } from '../../vitest.native.setup';
import { Button } from './button';

describe('Button (native leaf)', () => {
  it('renders as a button and fires onPress', async () => {
    const user = userEvent.setup();
    const onPress = vi.fn();

    render(<Button onPress={onPress}>Join the waitlist</Button>);

    const button = screen.getByRole('button', { name: 'Join the waitlist' });
    await user.click(button);

    expect(onPress).toHaveBeenCalledTimes(1);
  });

  it('resolves light colors when the OS scheme is light', () => {
    render(<Button>Join the waitlist</Button>);

    const button = screen.getByRole('button', { name: 'Join the waitlist' });
    expect(rgb(button.style.backgroundColor)).toEqual(rgb(colors.light.primary));
  });

  // The 0.2.1 regression: every native leaf baked in `colors.light` at module
  // load, so a dark-mode app rendered light design-system surfaces. Colors
  // must resolve from the scheme at render time.
  it('resolves dark colors when the OS scheme is dark', () => {
    setPrefersColorScheme('dark');

    render(<Button>Join the waitlist</Button>);

    const button = screen.getByRole('button', { name: 'Join the waitlist' });
    expect(rgb(button.style.backgroundColor)).toEqual(rgb(colors.dark.primary));
  });
});
