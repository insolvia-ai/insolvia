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
  await screen.findByText(/Cognito subject/);
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

  it('opens the API panel with a level-2 heading, not a second h1', async () => {
    // `heading-order` is one of the rules the axe gate fails on, and a heading
    // chosen for how big it should look is what breaks it.
    await renderSignedInHome();

    const heading = screen.getByRole('heading', { name: 'Your API session' });
    expect(heading.props['aria-level']).toBe(2);
  });

  it('reflects the resolved environment in the badge and the body', async () => {
    await renderSignedInHome();

    // Tests run without EXPO_PUBLIC_INSOLVIA_ENV, so this is the `local`
    // fallback arm — the same one an unconfigured build takes.
    const env = environmentInfo(appEnvironment);
    expect(env.name).toBe('local');

    expect(screen.getByText(env.label.toUpperCase())).toBeTruthy();
    expect(screen.getByLabelText(`${env.label} environment, ${env.host}`)).toBeTruthy();
    expect(screen.getByText(`Serving ${env.label.toLowerCase()} · ${env.host}`)).toBeTruthy();
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

  it("shows the user's email address as visible text", async () => {
    // From the ID token's `email` claim. The end-to-end suite matches on the
    // address being on screen, so this is a contract, not decoration.
    await renderSignedInHome();

    expect(screen.getByText(TEST_EMAIL)).toBeTruthy();
  });

  it('never renders the Cognito username where an email belongs', async () => {
    // `/v1/me`'s `username` is a UUID, because the pool uses
    // `username_attributes = ["email"]`. Rendering it as an account label would
    // be a plausible-looking lie.
    await renderSignedInHome();
    expect(screen.getByText(TEST_EMAIL)).toBeTruthy();

    expect(screen.queryByText(/^00000000-0000-4000-8000-000000000001$/)).toBeNull();
  });

  it('renders the claims GET /v1/me returned', async () => {
    await renderSignedInHome();

    // The whole authenticated loop, proven: hosted-UI token → Authorization:
    // Bearer → a JWT the API verified → claims on screen.
    expect(screen.getByText(/Cognito subject: 00000000-0000-4000-8000-000000000001/)).toBeTruthy();
    expect(screen.getByText(/Scopes: openid, email, profile/)).toBeTruthy();
  });

  it('sends the access token as a bearer credential, never the ID token', async () => {
    await renderSignedInHome();

    const call = (globalThis.fetch as unknown as jest.Mock).mock.calls.find(
      ([url]: [string]) => typeof url === 'string' && url.includes('/v1/me'),
    ) as [string, RequestInit] | undefined;

    const headers = call?.[1].headers as Record<string, string> | undefined;
    expect(headers?.Authorization).toBe('Bearer test-access-token');
  });

  it('offers a button named exactly "Sign out"', async () => {
    // Another accessible name the end-to-end suite matches verbatim.
    await renderSignedInHome();

    expect(screen.getByRole('button', { name: 'Sign out' })).toBeTruthy();
  });

  it('clears storage and leaves for the hosted logout endpoint when pressed', async () => {
    await renderSignedInHome();

    await userEvent.press(screen.getByRole('button', { name: 'Sign out' }));

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
