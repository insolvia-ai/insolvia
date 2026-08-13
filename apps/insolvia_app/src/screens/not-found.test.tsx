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
 * This route is what answers a mistyped URL in production: CloudFront rewrites
 * 403/404 to `/index.html` with HTTP 200, so the edge never 404s and the router
 * is the only thing that can say "not found".
 *
 * It is deliberately **not** behind the session guard. A signed-out visitor who
 * mistypes a URL should be told the page does not exist, rather than bounced to
 * sign-in — which would imply the page exists and they merely lack access.
 */
describe('an unknown path', () => {
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

  it('renders branded not-found chrome that echoes the path', async () => {
    renderRouter('src/app', { initialUrl: '/nope' });

    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeTruthy();
    expect(screen.getByText('Nothing lives at /nope.')).toBeTruthy();
    expect(screen.getByText('Insolvia.')).toBeTruthy();
    // No session, so the shell offers no account controls.
    expect(screen.queryByRole('button', { name: 'Sign out' })).toBeNull();
  });

  it('leads a signed-in user back to home', async () => {
    // Signed in the way a reload does it, so `/` resolves past the guard.
    writeRefreshToken('stored-refresh-token');

    const router = renderRouter('src/app', { initialUrl: '/nope' });

    await userEvent.press(await screen.findByRole('button', { name: 'Back to home' }));

    // `getPathname()` rather than the `toHavePathname` matcher: the matcher is
    // registered at runtime but expo-router ships no type declaration for it,
    // so it would not typecheck.
    expect(router.getPathname()).toBe('/');
    // Waits for the shell's account control too, so no state update lands
    // after the test. It used to wait on the API panel's claims, which have
    // moved to /account.
    expect(await screen.findByRole('button', { name: 'Account menu' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Your case workspace' })).toBeTruthy();
  });

  it('sends a signed-out user from home on to sign-in', async () => {
    const router = renderRouter('src/app', { initialUrl: '/nope' });

    await userEvent.press(await screen.findByRole('button', { name: 'Back to home' }));

    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeTruthy();
    expect(router.getPathname()).toBe('/sign-in');
  });

  it('does not swallow the routes that do exist', async () => {
    renderRouter('src/app', { initialUrl: '/auth/callback' });

    // The callback route now completes a real exchange, so with no pending
    // attempt in storage it reports a failure rather than a placeholder.
    expect(
      await screen.findByRole('heading', { name: 'Sign-in could not be completed' }),
    ).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Page not found' })).toBeNull();
  });

  it('answers /sign-in with the sign-in screen', async () => {
    renderRouter('src/app', { initialUrl: '/sign-in' });

    expect(await screen.findByRole('heading', { name: 'Sign in to Insolvia' })).toBeTruthy();
  });
});
