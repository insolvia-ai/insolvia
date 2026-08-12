import { screen, userEvent } from '@testing-library/react-native';
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

  it('falls back to the email for initials when there is no name yet', async () => {
    // `principalResponse()` carries no firm, so there is no display name — the
    // state a member sits in before `RequireProfile` has their name. An empty
    // circle would be worse than two letters from the address.
    signedIn();

    expect(await screen.findByText('AT')).toBeTruthy();
  });
});
