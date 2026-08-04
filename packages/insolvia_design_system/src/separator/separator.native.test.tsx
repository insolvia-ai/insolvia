// NATIVE-leaf test — see button.native.test.tsx for how the `native` vitest
// project resolves './separator' to separator.native.tsx and renders it
// through react-native-web. Separator carries no interaction, so the one
// piece of real wiring worth pinning is the 0.2.1-style dark-mode regression:
// color must resolve from useNativeColors() at render time, not a
// module-load-time `colors.light`.
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { colors } from '@insolvia-ai/tokens';

import { rgb, setPrefersColorScheme } from '../../vitest.native.setup';
import { Separator } from './separator';

describe('Separator (native leaf)', () => {
  it('resolves dark line color when the OS scheme is dark', () => {
    setPrefersColorScheme('dark');

    render(<Separator testID="case-separator" />);

    const separator = screen.getByTestId('case-separator');
    expect(rgb(separator.style.backgroundColor)).toEqual(rgb(colors.dark.line));
  });

  it('resolves light line color when the OS scheme is light', () => {
    render(<Separator testID="case-separator" />);

    const separator = screen.getByTestId('case-separator');
    expect(rgb(separator.style.backgroundColor)).toEqual(rgb(colors.light.line));
  });
});
