import { act, screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  installFakeBrowser,
  principalResponse,
  routeFetch,
  TEST_AUTH_CONFIG,
  TEST_EMAIL,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';
import { brandColors } from '@/theme/brand-colors';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

/**
 * The account menu's OPEN AND CLOSE behaviour, which is the part the design
 * system cannot supply.
 *
 * Its dropdown closes on an item press or a second trigger press and nothing
 * else: React Native has no document to listen to for an outside press, and
 * the package's native leaf says so in as many words. So the shell renders a
 * full-screen press target and closes on navigation, and both of those are
 * app-owned behaviour with nothing upstream to lean on — which is exactly what
 * earns a test.
 */
describe('the account menu', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(initialUrl = '/') {
    globalThis.fetch = jest.fn(
      routeFetch({
        '/oauth2/token': () => tokenEndpointResponse(),
        '/v1/me': () => principalResponse(),
      }),
    ) as unknown as typeof fetch;
    return renderRouter('src/app', { initialUrl });
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

  async function ready() {
    return screen.findByRole('button', { name: 'Account menu' });
  }

  it('reports its expanded state, which is the whole of its a11y contract', async () => {
    // The trigger is ours rather than `Dropdown.Trigger` — that part wraps its
    // children in a `Text` and so cannot hold an Avatar — so the aria wiring
    // the part would have contributed is this component's to get right.
    signedIn();
    const user = userEvent.setup();
    const trigger = await ready();

    // `accessibilityState` is what this native environment reads; the flat
    // `aria-expanded` beside it is what reaches the DOM through
    // react-native-web on the web build. The component sets both, so this
    // asserts the one this environment can see.
    expect(trigger.props.accessibilityState?.expanded).toBe(false);

    await user.press(screen.getByRole('button', { name: 'Account menu' }));

    expect(
      screen.getByRole('button', { name: 'Account menu' }).props.accessibilityState?.expanded,
    ).toBe(true);
  });

  it('closes on a second press of the trigger', async () => {
    signedIn();
    const user = userEvent.setup();
    await ready();

    await user.press(screen.getByRole('button', { name: 'Account menu' }));
    expect(screen.getByText(TEST_EMAIL)).toBeTruthy();

    await user.press(screen.getByRole('button', { name: 'Account menu' }));
    expect(screen.queryByText(TEST_EMAIL)).toBeNull();
  });

  it('closes when the page behind it is pressed', async () => {
    // THE BEHAVIOUR THE PACKAGE CANNOT PROVIDE. AppShell renders a full-screen
    // press target while the menu is open, as a sibling of the whole page —
    // inside the header it would be clipped to the header's own box, because
    // react-native-web gives every View `position: relative`.
    signedIn();
    const user = userEvent.setup();
    await ready();
    await user.press(screen.getByRole('button', { name: 'Account menu' }));
    expect(screen.getByText(TEST_EMAIL)).toBeTruthy();

    // By test id, because the layer is deliberately absent from the
    // accessibility tree and so has no role or name to be found by. AppShell's
    // comment owns why that trade is the right way round.
    // `includeHiddenElements` because the layer sets
    // `accessibilityElementsHidden`, and RNTL skips such elements by default —
    // which is the library agreeing with the design: it is a pointer
    // affordance, invisible to assistive tech on purpose.
    await user.press(screen.getByTestId('account-menu-dismiss', { includeHiddenElements: true }));

    expect(screen.queryByText(TEST_EMAIL)).toBeNull();
  });

  it('closes on Escape from wherever focus is', async () => {
    // Nothing did this before: the package's native leaf has no document to
    // listen to, and a comment in the shell claimed the trigger handled it. A
    // keyboard user who had tabbed into the menu and changed their mind was
    // stuck pressing something. The listener is document-level, which is what
    // `pressKey` drives — `userEvent` types into a focused element, and the
    // whole point is that focus may be anywhere.
    signedIn();
    const user = userEvent.setup();
    await ready();

    await user.press(screen.getByRole('button', { name: 'Account menu' }));
    expect(screen.getByText(TEST_EMAIL)).toBeTruthy();

    // A document event lands outside React's scheduler, so the state change
    // it causes is wrapped the way `userEvent` wraps its own.
    act(() => {
      browser.pressKey('Escape');
    });

    expect(screen.queryByText(TEST_EMAIL)).toBeNull();
    expect(
      screen.getByRole('button', { name: 'Account menu' }).props.accessibilityState?.expanded,
    ).toBe(false);
  });

  it('closes when the route changes', async () => {
    // A menu that survived a navigation would hang over a screen the user has
    // already moved on from.
    signedIn();
    const user = userEvent.setup();
    await ready();
    await user.press(screen.getByRole('button', { name: 'Account menu' }));

    await user.press(screen.getByRole('menuitem', { name: 'Your account' }));

    expect(screen.queryByText(TEST_EMAIL)).toBeNull();
  });

  it('names the environment, spelled out, inside the identity block', async () => {
    // The header pill that said "LOCAL" is gone; this is where the answer to
    // "which deployment am I signed in to" lives now, in the same words a
    // screen reader gets. Tests run without EXPO_PUBLIC_INSOLVIA_ENV, so this
    // is the `local` arm — the same one an unconfigured build takes.
    signedIn();
    const user = userEvent.setup();
    await ready();

    await user.press(screen.getByRole('button', { name: 'Account menu' }));

    expect(screen.getByText('Local environment · localhost')).toBeTruthy();
  });

  /**
   * The colour-scheme preference, which used to be a cycling button in the
   * header and is three menu items now.
   *
   * THE ONE THING WORTH PINNING HARDEST is that the choice reaches the DESIGN
   * SYSTEM, not just this app's own components. Its `.native` leaves — which
   * this app renders on every platform — call React Native's `useColorScheme()`
   * themselves, and react-native-web implements that as a `prefers-color-scheme`
   * media query with no setter. So the only way to move them is a
   * `ThemeProvider` whose `light` and `dark` slots both hold the chosen palette,
   * and a test that only checked app-owned chrome would pass with that seam
   * removed. The `Button` below is a design-system component; its rendered
   * colour is the assertion.
   */
  describe('appearance', () => {
    async function openMenu(user: ReturnType<typeof userEvent.setup>) {
      await ready();
      await user.press(screen.getByRole('button', { name: 'Account menu' }));
    }

    it('offers three choices and marks the device setting as current, in the name', async () => {
      // THREE, not two: "follow device" is what a phone that goes dark in the
      // evening needs. The tick is part of the accessible name, because
      // `Dropdown.Item` exposes no checked state — the name is the only
      // channel a screen reader has for "this is the one you are on".
      signedIn();
      const user = userEvent.setup();
      await openMenu(user);

      expect(screen.getByRole('menuitem', { name: 'Follow device ✓' })).toBeTruthy();
      expect(screen.getByRole('menuitem', { name: 'Light' })).toBeTruthy();
      expect(screen.getByRole('menuitem', { name: 'Dark' })).toBeTruthy();
    });

    it('moves the DESIGN SYSTEM’s components, not only our own', async () => {
      // Remove the `ThemeProvider` from `ThemePreferenceProvider` and this is
      // the test that fails while everything else still passes. It asserts the
      // BRAND value, not the tokens default: from tokens 0.5.0 the package's
      // base theme is deliberately unbranded, so a `ThemeProvider` that passed
      // nothing would render the package's monochrome primary here.
      signedIn();
      const user = userEvent.setup();

      const cta = await screen.findByRole('button', { name: 'Start a case' });
      expect(flattenedBackground(cta)).toBe(brandColors.light.primary);

      await openMenu(user);
      await user.press(screen.getByRole('menuitem', { name: 'Dark' }));

      expect(flattenedBackground(screen.getByRole('button', { name: 'Start a case' }))).toBe(
        brandColors.dark.primary,
      );
      // Choosing closes the menu, like every other item; reopening shows the
      // tick moved.
      await openMenu(user);
      expect(screen.getByRole('menuitem', { name: 'Dark ✓' })).toBeTruthy();
    });

    it('remembers the choice across a reload', async () => {
      // It is stored in `localStorage` and read synchronously in the state
      // initialiser rather than in an effect — an effect would paint one frame
      // in the device's scheme before correcting itself, which is the flash
      // the preference exists to avoid.
      signedIn();
      const user = userEvent.setup();
      await openMenu(user);
      await user.press(screen.getByRole('menuitem', { name: 'Light' }));

      screen.unmount();
      signedIn();
      await openMenu(user);

      expect(screen.getByRole('menuitem', { name: 'Light ✓' })).toBeTruthy();
    });
  });

  it('falls back to the email for initials when there is no name yet', async () => {
    // `principalResponse()` carries no firm, so there is no display name — the
    // state a member sits in before `RequireProfile` has their name. An empty
    // circle would be worse than two letters from the address.
    signedIn();

    expect(await screen.findByText('AT')).toBeTruthy();
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
