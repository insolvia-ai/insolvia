import { colors } from '@insolvia-ai/tokens';
import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  installFakeBrowser,
  principalResponse,
  routeFetch,
  TEST_AUTH_CONFIG,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

/**
 * The header's colour-scheme control, and the preference behind it.
 *
 * THE ONE THING WORTH PINNING HARDEST is that the choice reaches the DESIGN
 * SYSTEM, not just this app's own components. Its `.native` leaves — which
 * this app renders on every platform — call React Native's `useColorScheme()`
 * themselves, and react-native-web implements that as a `prefers-color-scheme`
 * media query with no setter. So the only way to move them is a `ThemeProvider`
 * whose `light` and `dark` slots both hold the chosen palette, and a test that
 * only checked app-owned chrome would pass with that seam removed.
 *
 * The `Button` below is a design-system component; its rendered colour is the
 * assertion.
 */
describe('the theme toggle', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn() {
    globalThis.fetch = jest.fn(
      routeFetch({
        '/oauth2/token': () => tokenEndpointResponse(),
        '/v1/me': () => principalResponse(),
      }),
    ) as unknown as typeof fetch;
    return renderRouter('src/app', { initialUrl: '/' });
  }

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    browser.restore();
    jest.clearAllMocks();
  });

  it('starts on the device setting and says so', async () => {
    signedIn();

    expect(
      await screen.findByRole('button', { name: 'Theme: following your device. Switch to light.' }),
    ).toBeTruthy();
  });

  it('cycles system → light → dark → system, naming each state', async () => {
    // THREE states, not two. A plain light/dark toggle cannot express "follow
    // my device", which is what a phone that goes dark in the evening needs —
    // and the accessible name has to change with the state, because for a
    // cycling control the name IS the purpose (WCAG 2.4.6).
    signedIn();
    const user = userEvent.setup();
    // Settle the session bootstrap FIRST. After that every lookup is
    // synchronous: awaiting a `findBy*` inside `press()` resolves a node that
    // the next re-render replaces, and pressing it fails on an unmounted tree.
    await screen.findByRole('button', { name: 'Theme: following your device. Switch to light.' });

    await user.press(
      screen.getByRole('button', { name: 'Theme: following your device. Switch to light.' }),
    );
    await user.press(screen.getByRole('button', { name: 'Theme: light. Switch to dark.' }));
    await user.press(
      screen.getByRole('button', { name: 'Theme: dark. Follow your device instead.' }),
    );

    expect(
      screen.getByRole('button', { name: 'Theme: following your device. Switch to light.' }),
    ).toBeTruthy();
  });

  it('moves the DESIGN SYSTEM’s components, not only our own', async () => {
    // The seam that makes the whole feature work. The package's leaves ask the
    // OS which palette to use; the app answers by making both palettes the
    // same one. Remove the `ThemeProvider` from `ThemePreferenceProvider` and
    // this is the test that fails while everything else still passes.
    signedIn();
    const user = userEvent.setup();

    const cta = await screen.findByRole('button', { name: 'Start a case' });
    expect(flattenedBackground(cta)).toBe(colors.light.primary);

    await user.press(
      screen.getByRole('button', { name: 'Theme: following your device. Switch to light.' }),
    );
    await user.press(screen.getByRole('button', { name: 'Theme: light. Switch to dark.' }));

    expect(flattenedBackground(screen.getByRole('button', { name: 'Start a case' }))).toBe(
      colors.dark.primary,
    );
  });

  it('remembers the choice across a reload', async () => {
    // It is stored in `localStorage` and read synchronously in the state
    // initialiser rather than in an effect — an effect would paint one frame in
    // the device's scheme before correcting itself, which is the flash the
    // preference exists to avoid.
    signedIn();
    const user = userEvent.setup();
    await screen.findByRole('button', { name: 'Theme: following your device. Switch to light.' });
    await user.press(
      screen.getByRole('button', { name: 'Theme: following your device. Switch to light.' }),
    );
    expect(screen.getByRole('button', { name: 'Theme: light. Switch to dark.' })).toBeTruthy();

    screen.unmount();
    signedIn();

    expect(
      await screen.findByRole('button', { name: 'Theme: light. Switch to dark.' }),
    ).toBeTruthy();
  });
});

/** The `backgroundColor` a component resolved to, through RN's style array. */
function flattenedBackground(node: { props: { style?: unknown } }): string | undefined {
  const flatten = (style: unknown): Record<string, unknown> => {
    if (Array.isArray(style))
      return Object.assign({}, ...style.map(flatten)) as Record<string, unknown>;
    return (style ?? {}) as Record<string, unknown>;
  };
  return flatten(node.props.style).backgroundColor as string | undefined;
}
