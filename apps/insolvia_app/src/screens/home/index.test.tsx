import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import { appEnvironment, environmentInfo } from '@/config/environment';
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

type RenderedNode = ReturnType<typeof screen.toJSON>;

/** Every `role` prop in a rendered tree, in document order. */
function rolesIn(node: RenderedNode): string[] {
  if (node === null || typeof node !== 'object') return [];
  const nodes = Array.isArray(node) ? node : [node];
  return nodes.flatMap((child) => {
    const role: unknown = child.props?.role;
    const here = typeof role === 'string' ? [role] : [];
    return [...here, ...(child.children ?? []).flatMap((c) => rolesIn(c as RenderedNode))];
  });
}

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

/**
 * Renders `/` and waits until **everything** async has settled — the session
 * bootstrap and the `GET /v1/me` call the shell fires after it.
 *
 * Waiting for the last of them, rather than for whatever a given test happens
 * to assert on, is what keeps React from reporting a state update outside
 * `act(...)`: a test that returns while the API call is still in flight leaves
 * a `setState` to land against an unmounted tree.
 */
async function renderSignedInHome() {
  const router = renderRouter('src/app', { initialUrl: '/' });
  await screen.findByRole('button', { name: 'Account menu' });
  // NOTHING ON THIS SCREEN RENDERS FROM `/v1/me` ANY MORE — MePanel's claim
  // rows, which used to be the settle signal, moved to /account. So the
  // request itself is the signal, and `waitFor` is what makes it one: it
  // retries inside `act(...)`, flushing the response and the state update
  // behind it. Returning before that lands leaves a `setState` to fire against
  // an unmounted tree, which is the warning this helper exists to prevent.
  await waitFor(() => {
    expect(
      (globalThis.fetch as unknown as jest.Mock).mock.calls.filter(([url]) =>
        String(url).includes('/v1/me'),
      ),
    ).toHaveLength(1);
  });
  return router;
}

/**
 * The home screen, and that `/` lands on it.
 *
 * These render through the **real router** (`renderRouter` mounts `src/app`),
 * so a route file that moved or stopped compiling fails here.
 *
 * `/` is now a **protected** route, so every test in this file first has to
 * produce a signed-in session — which it does the way a real reload does: a
 * refresh token in `localStorage`, exchanged at the hosted UI's token endpoint
 * during bootstrap. Nothing here injects a session directly, so the guard, the
 * provider and the screen are all exercised together.
 */
describe('the home route', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
    globalThis.fetch = jest.fn(
      routeFetch({
        '/oauth2/token': () => tokenEndpointResponse(),
        '/v1/me': () => principalResponse(),
      }),
    ) as unknown as typeof fetch;
  });

  afterEach(() => {
    browser.restore();
    globalThis.fetch = realFetch;
  });

  it('renders the branded chrome and the shell content at /', async () => {
    await renderSignedInHome();

    // The wordmark is the shell's identity — it comes from AppShell, so this
    // also asserts the screen is inside the frame.
    expect(screen.getByText('Insolvia.')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Your case workspace' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Start a case' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Your cases' })).toBeTruthy();
  });

  it('gives the page exactly one level-1 heading', async () => {
    await renderSignedInHome();

    const levelOne = screen
      .getAllByRole('heading')
      .filter((node) => node.props['aria-level'] === 1);

    expect(levelOne).toHaveLength(1);
  });

  it('no longer carries the API session panel', async () => {
    // It was the pipeline proof (issue #77) and is support detail now — a
    // Cognito subject and an app-client id mean nothing to somebody preparing
    // a petition. It lives collapsed at the bottom of /account; see MePanel.
    await renderSignedInHome();

    expect(screen.queryByRole('heading', { name: 'Support details' })).toBeNull();
    expect(screen.queryByText(/Cognito subject/)).toBeNull();
  });

  it('reflects the resolved environment in the badge, and only there', async () => {
    await renderSignedInHome();

    // Tests run without EXPO_PUBLIC_INSOLVIA_ENV, so this is the `local`
    // fallback arm — the same one an unconfigured build takes.
    const env = environmentInfo(appEnvironment);
    expect(env.name).toBe('local');

    expect(screen.getByText(env.label.toUpperCase())).toBeTruthy();
    expect(screen.getByLabelText(`${env.label} environment, ${env.host}`)).toBeTruthy();
    // The body used to repeat this as "Serving local · localhost". The badge
    // above already says it, and the footer's build stamp says it with the
    // bundle id attached — so the prose line went, and this asserts it stayed
    // gone rather than merely not asserting it.
    expect(screen.queryByText(/^Serving /)).toBeNull();
  });

  it('keeps the decorative arrow glyph out of the primary CTA accessible name', async () => {
    // The design-system button has no `icon` prop, so the glyph is an
    // `aria-hidden` child at the call site and `aria-label` pins the name. If
    // either half were dropped, a screen reader would announce "Start a case
    // right arrow".
    await renderSignedInHome();

    // RN aliases `aria-label` onto `accessibilityLabel` on the host element,
    // which is the prop react-native-web emits as `aria-label` in the DOM.
    const cta = screen.getByRole('button', { name: 'Start a case' });
    expect(cta.props.accessibilityLabel).toBe('Start a case');

    // The glyph renders (visible when hidden elements are included) but is
    // excluded from the accessibility tree.
    expect(screen.queryByText('→')).toBeNull();
    expect(screen.getByText('→', { includeHiddenElements: true })).toBeTruthy();
  });

  it('sends the primary CTA to the case list', async () => {
    // This used to answer with a "case tools arrive in a later release"
    // notice. It now navigates, because the thing that notice apologised for
    // exists (issue 8.3). Both buttons go to the same place; the presence of
    // the secondary one is asserted above, and pressing it here would only
    // re-test expo-router.
    const router = await renderSignedInHome();

    await userEvent.press(screen.getByRole('button', { name: 'Start a case' }));

    // `getPathname()` rather than the `toHavePathname` matcher, for the reason
    // auth-callback.test.tsx gives: the matcher is registered at runtime but
    // expo-router ships no type declaration for it.
    await waitFor(() => {
      expect(router.getPathname()).toBe('/cases');
    });
  });

  it('frames every screen with the header, nav, main and footer landmarks', async () => {
    await renderSignedInHome();

    // react-native-web maps these four roles to <header>, <nav>, <main> and
    // <footer>. The built DOM is re-checked in the deploy verification; this is
    // the guard that stops the roles being dropped from AppShell in the first
    // place.
    //
    // Walked from the rendered tree rather than queried with `getByRole`:
    // Testing Library only matches *accessibility elements*, and a landmark
    // `View` is not one — making it one (`accessible`) would collapse the whole
    // header into a single element for a screen reader on native, which is a
    // worse app in exchange for a prettier test.
    expect(rolesIn(screen.toJSON())).toEqual(
      expect.arrayContaining(['banner', 'navigation', 'main', 'contentinfo']),
    );
  });
});

describe('the signed-in shell', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
    globalThis.fetch = jest.fn(
      routeFetch({
        '/oauth2/token': () => tokenEndpointResponse(),
        '/v1/me': () => principalResponse(),
      }),
    ) as unknown as typeof fetch;
  });

  afterEach(() => {
    browser.restore();
    globalThis.fetch = realFetch;
  });

  /** Opens the account menu, which now holds the identity and the way out. */
  async function openAccountMenu() {
    await renderSignedInHome();
    await userEvent.press(screen.getByRole('button', { name: 'Account menu' }));
  }

  it('keeps the identity out of the header until the menu is opened', async () => {
    // The header used to carry the address as plain text beside an Account
    // link and a full-size Sign out button — most of its height, for something
    // touched rarely. One avatar replaces all three.
    await renderSignedInHome();

    expect(screen.queryByText(TEST_EMAIL)).toBeNull();
    expect(screen.getByRole('button', { name: 'Account menu' })).toBeTruthy();
  });

  it("shows the user's email address in the menu", async () => {
    // From the ID token's `email` claim, never `/v1/me` — the pool's
    // `username_attributes = ["email"]` means no access-token claim carries it.
    await openAccountMenu();

    expect(screen.getByText(TEST_EMAIL)).toBeTruthy();
  });

  it('never renders the Cognito username where an email belongs', async () => {
    // `/v1/me`'s `username` is a UUID. Rendering it as an account label would
    // be a plausible-looking lie.
    await openAccountMenu();
    expect(screen.getByText(TEST_EMAIL)).toBeTruthy();

    expect(screen.queryByText(/^00000000-0000-4000-8000-000000000001$/)).toBeNull();
  });

  it('sends the access token as a bearer credential, never the ID token', async () => {
    await renderSignedInHome();

    const call = (globalThis.fetch as unknown as jest.Mock).mock.calls.find(
      ([url]: [string]) => typeof url === 'string' && url.includes('/v1/me'),
    ) as [string, RequestInit] | undefined;

    const headers = call?.[1].headers as Record<string, string> | undefined;
    expect(headers?.Authorization).toBe('Bearer test-access-token');
  });

  it('offers a menu item named exactly "Sign out"', async () => {
    // Another accessible name the end-to-end suite matches verbatim — and it
    // is a `menuitem` now rather than a button, which is the coordinated half
    // of that change.
    await openAccountMenu();

    expect(screen.getByRole('menuitem', { name: 'Sign out' })).toBeTruthy();
  });

  it('clears storage and leaves for the hosted logout endpoint when pressed', async () => {
    await openAccountMenu();

    await userEvent.press(screen.getByRole('menuitem', { name: 'Sign out' }));

    // Leg one: nothing persisted survives.
    await waitFor(() => {
      expect(browser.localStorage.entries.size).toBe(0);
    });

    // Leg two: Cognito's own session cookie is ended too, or the next sign-in
    // would silently re-authenticate the same user.
    const url = browser.navigations.at(-1) ?? '';
    expect(url.startsWith(`${TEST_AUTH_CONFIG.domain}/logout?`)).toBe(true);
    expect(url).toContain('logout_uri=');
  });
});
