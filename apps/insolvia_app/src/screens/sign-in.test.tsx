import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  installFakeBrowser,
  principalResponse,
  routeFetch,
  TEST_AUTH_CONFIG,
  tokenEndpointError,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

/**
 * The build's hosted-UI configuration, swapped per test.
 *
 * `_layout.tsx` mounts `SessionProvider` with no props, so a route-level test
 * cannot pass a config in — it has to change what the provider resolves. The
 * name must begin with `mock` for Jest's hoisting whitelist; the arrow reads it
 * at render time, long after this module has initialised.
 */
let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

describe('the sign-in route', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
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

  it('offers a button named exactly "Sign in"', async () => {
    // The accessible name is a contract with the end-to-end suite, which
    // matches on it verbatim.
    renderRouter('src/app', { initialUrl: '/sign-in' });

    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Sign in to Insolvia' })).toBeTruthy();
  });

  it('gives the page exactly one level-1 heading', async () => {
    renderRouter('src/app', { initialUrl: '/sign-in' });
    await screen.findByRole('button', { name: 'Sign in' });

    const levelOne = screen.getAllByRole('heading').filter((n) => n.props['aria-level'] === 1);
    expect(levelOne).toHaveLength(1);
  });

  it('renders no password field — the hosted UI owns credentials', async () => {
    // If a local credential form ever appeared here it would mean the app had
    // started handling passwords, which the authorization-code flow exists to
    // prevent.
    renderRouter('src/app', { initialUrl: '/sign-in' });
    await screen.findByRole('button', { name: 'Sign in' });

    expect(screen.queryByLabelText(/password/i)).toBeNull();
  });

  it('leaves for the hosted UI with a PKCE challenge when pressed', async () => {
    renderRouter('src/app', { initialUrl: '/sign-in' });

    await userEvent.press(await screen.findByRole('button', { name: 'Sign in' }));

    await waitFor(() => {
      expect(browser.navigations).toHaveLength(1);
    });
    const url = browser.navigations[0] ?? '';
    expect(url.startsWith(`${TEST_AUTH_CONFIG.domain}/oauth2/authorize?`)).toBe(true);
    expect(url).toContain('code_challenge_method=S256');
    expect(url).toContain('response_type=code');
  });

  describe('when the environment has no hosted UI', () => {
    beforeEach(() => {
      mockAuthConfig = null;
    });

    it('says so, accessibly, instead of offering a broken redirect', async () => {
      // The `local` default. A button here would redirect to
      // `https://undefined/oauth2/authorize`.
      renderRouter('src/app', { initialUrl: '/sign-in' });

      const heading = await screen.findByRole('heading', { name: 'Sign-in is not configured' });
      expect(heading.props['aria-level']).toBe(1);

      const message = screen.getByText(/not configured for this environment/i);
      // Announced, not merely drawn: a screen-reader user gets no signal from a
      // layout change.
      expect(message.props['aria-live']).toBe('assertive');

      expect(screen.queryByRole('button', { name: 'Sign in' })).toBeNull();
      expect(browser.navigations).toHaveLength(0);
    });
  });
});

describe('the route guard', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
  });

  afterEach(() => {
    browser.restore();
    globalThis.fetch = realFetch;
  });

  it('sends a signed-out visitor from a protected route to sign-in', async () => {
    globalThis.fetch = jest.fn(routeFetch({})) as unknown as typeof fetch;

    const router = renderRouter('src/app', { initialUrl: '/' });

    await waitFor(() => {
      expect(router.getPathname()).toBe('/sign-in');
    });
    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeTruthy();
    // The protected screen never rendered.
    expect(screen.queryByRole('heading', { name: 'Your case workspace' })).toBeNull();
  });

  it('remembers where the visitor was headed', async () => {
    globalThis.fetch = jest.fn(routeFetch({})) as unknown as typeof fetch;

    const router = renderRouter('src/app', { initialUrl: '/' });

    await waitFor(() => {
      expect(router.getPathname()).toBe('/sign-in');
    });
    // Carried as `returnTo` so a deep link survives the detour.
    expect(router.getSearchParams()).toMatchObject({ returnTo: '/' });
  });

  it('renders no protected data, and no sign-in, while the session is being restored', async () => {
    // Issue #78's acceptance criterion, restated for the optimistic guard. The
    // refresh never settles, so the session is pinned in its restore window.
    // The app's chrome may render — that is the point — but nothing may be
    // FETCHED, because a fetch is the only way case data reaches the screen,
    // and the user must not be bounced to sign-in either.
    writeRefreshToken('stored-refresh-token');
    const fetchMock = jest.fn(
      (_url: string, _init?: unknown) => new Promise<Response>(() => undefined),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    const router = renderRouter('src/app', { initialUrl: '/' });

    expect(await screen.findByRole('heading', { name: 'Your case workspace' })).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Checking your session' })).toBeNull();
    // Only the token endpoint has been asked anything: the API waits on it.
    const urls = fetchMock.mock.calls.map((call) => String(call[0]));
    expect(urls.every((url) => url.includes('/oauth2/token'))).toBe(true);
    expect(urls).toHaveLength(1);
    // And it has NOT bounced an about-to-be-signed-in user to sign-in either.
    expect(router.getPathname()).toBe('/');
    expect(screen.queryByRole('button', { name: 'Sign in' })).toBeNull();
  });

  it('waits for a restore before bouncing a signed-in visitor off /sign-in', async () => {
    // A stale stored token would otherwise send the visitor to `/` and straight
    // back here. The page stays empty for the deferral, then names itself.
    writeRefreshToken('stored-refresh-token');
    globalThis.fetch = jest.fn(
      () => new Promise<Response>(() => undefined),
    ) as unknown as typeof fetch;

    const router = renderRouter('src/app', { initialUrl: '/sign-in' });

    expect(screen.queryByRole('heading')).toBeNull();
    expect(await screen.findByRole('heading', { name: 'Checking your session' })).toBeTruthy();
    expect(router.getPathname()).toBe('/sign-in');
    expect(screen.queryByRole('button', { name: 'Sign in' })).toBeNull();
  });

  it('sends a visitor with a stale stored token to sign in', async () => {
    writeRefreshToken('stale-refresh-token');
    globalThis.fetch = jest.fn(
      routeFetch({ '/oauth2/token': () => tokenEndpointError() }),
    ) as unknown as typeof fetch;

    const router = renderRouter('src/app', { initialUrl: '/' });

    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeTruthy();
    expect(router.getPathname()).toBe('/sign-in');
    expect(router.getSearchParams()).toMatchObject({ returnTo: '/' });
  });

  it('lets a restored session through to the protected screen', async () => {
    writeRefreshToken('stored-refresh-token');
    globalThis.fetch = jest.fn(
      routeFetch({
        '/oauth2/token': () => tokenEndpointResponse(),
        '/v1/me': () => principalResponse(),
      }),
    ) as unknown as typeof fetch;

    const router = renderRouter('src/app', { initialUrl: '/' });

    expect(await screen.findByRole('heading', { name: 'Your case workspace' })).toBeTruthy();
    expect(router.getPathname()).toBe('/');
  });
});
