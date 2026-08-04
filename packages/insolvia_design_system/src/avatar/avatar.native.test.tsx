// NATIVE-leaf tests. react-native-web's Image resolves load/error through its
// own ImageLoader (a headless `Image()` instance, not a DOM element with
// load/error listeners attached) rather than a real <img> the way the web
// leaf's `avatar.test.tsx` can fireEvent on directly, and jsdom performs no
// actual resource loading — so unlike the web leaf, there is no reliable way
// to drive a real load/error round-trip here. These tests instead pin the
// wiring the 0.2.1-style regression would break: the context reaching
// Fallback correctly (idle by default, i.e. before/without a resolved image)
// and the native leaf resolving colors from the OS scheme at render time.
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { colors } from '@insolvia-ai/tokens';

import { rgb, setPrefersColorScheme } from '../../vitest.native.setup';
import { Avatar } from './avatar';

describe('Avatar (native leaf)', () => {
  it('shows the fallback when there is no image', () => {
    render(
      <Avatar.Root>
        <Avatar.Fallback>AS</Avatar.Fallback>
      </Avatar.Root>,
    );

    expect(screen.getByText('AS')).toBeVisible();
  });

  it('shows the fallback while the image has not yet resolved', () => {
    render(
      <Avatar.Root>
        <Avatar.Image source={{ uri: 'https://example.com/andreas.jpg' }} alt="Andreas Savva" />
        <Avatar.Fallback>AS</Avatar.Fallback>
      </Avatar.Root>,
    );

    expect(screen.getByText('AS')).toBeVisible();
  });

  // The 0.2.1 regression: every native leaf baked in `colors.light` at module
  // load, so a dark-mode app rendered light design-system surfaces. Colors
  // must resolve from the scheme at render time.
  it('resolves dark colors for the fallback when the OS scheme is dark', () => {
    setPrefersColorScheme('dark');

    render(
      <Avatar.Root>
        <Avatar.Fallback>AS</Avatar.Fallback>
      </Avatar.Root>,
    );

    const fallback = screen.getByText('AS').parentElement;
    expect(fallback).not.toBeNull();
    expect(rgb((fallback as HTMLElement).style.backgroundColor)).toEqual(
      rgb(colors.dark.surfaceAlt),
    );
  });
});
