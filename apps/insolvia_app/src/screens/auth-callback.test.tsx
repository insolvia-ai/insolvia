import { existsSync } from 'node:fs';
import path from 'node:path';

import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writePendingAuthorization } from '@/session';
import {
  installFakeBrowser,
  principalResponse,
  routeFetch,
  TEST_AUTH_CONFIG,
  tokenEndpointError,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

describe('the /auth/callback route', () => {
  it('is declared at exactly the path infra registers', () => {
    // A drift guard, and the one assertion in this file that is about the
    // filesystem rather than behaviour. `web_callback_urls` in
    // infra/modules/auth/main.tf is `"${o}/auth/callback"`, and under
    // file-based routing the path IS the file's location — so the assertion has
    // to be about the file. Changing one side without the other silently breaks
    // the return leg of sign-in.
    const routeFile = path.join(__dirname, '..', 'app', 'auth', 'callback.tsx');
    expect(existsSync(routeFile)).toBe(true);
  });
});

describe('completing sign-in at /auth/callback', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  /** Puts a pending attempt in storage, as `signIn()` would before redirecting. */
  function withPendingAttempt(state: string, returnTo: string | null = null) {
    writePendingAuthorization({ state, codeVerifier: 'test-code-verifier', returnTo });
  }

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

  it('shows an announced loading state while the exchange is in flight', async () => {
    globalThis.fetch = jest.fn(
      () => new Promise<Response>(() => undefined),
    ) as unknown as typeof fetch;
    withPendingAttempt('test-state');

    renderRouter('src/app', { initialUrl: '/auth/callback?code=test-code&state=test-state' });

    const heading = await screen.findByRole('heading', { name: 'Signing you in' });
    expect(heading.props['aria-level']).toBe(1);
    expect(screen.getByText(/Completing your sign-in/).props['aria-live']).toBe('polite');
  });

  it('exchanges the code and lands the user on the app', async () => {
    withPendingAttempt('test-state');

    const router = renderRouter('src/app', {
      initialUrl: '/auth/callback?code=test-code&state=test-state',
    });

    expect(await screen.findByRole('heading', { name: 'Your case workspace' })).toBeTruthy();
    expect(router.getPathname()).toBe('/');
    // Branded chrome, not a bare page.
    expect(screen.getByText('Insolvia.')).toBeTruthy();
  });

  it('returns the user to the route they were originally headed for', async () => {
    // A stale path still resolves through the router — `+not-found` answers if
    // it no longer exists — which is the correct outcome for an old deep link.
    withPendingAttempt('test-state', '/nope');

    const router = renderRouter('src/app', {
      initialUrl: '/auth/callback?code=test-code&state=test-state',
    });

    await waitFor(() => {
      expect(router.getPathname()).toBe('/nope');
    });
  });

  it('rejects a mismatched state with an announced error and no exchange', async () => {
    // RFC 6749 section 10.12. Not a silent retry: a code arriving under the
    // wrong `state` was not requested by this browser.
    withPendingAttempt('the-real-state');

    renderRouter('src/app', {
      initialUrl: '/auth/callback?code=test-code&state=an-attackers-state',
    });

    const heading = await screen.findByRole('heading', {
      name: 'Sign-in could not be completed',
    });
    expect(heading.props['aria-level']).toBe(1);
    expect(screen.getByText(/could not be verified/i).props['aria-live']).toBe('assertive');
    expect(globalThis.fetch).not.toHaveBeenCalled();
    expect(screen.queryByRole('heading', { name: 'Your case workspace' })).toBeNull();
  });

  it('rejects a callback with no pending attempt behind it', async () => {
    renderRouter('src/app', { initialUrl: '/auth/callback?code=test-code&state=test-state' });

    expect(
      await screen.findByRole('heading', { name: 'Sign-in could not be completed' }),
    ).toBeTruthy();
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it('reports a hosted-UI error without attempting an exchange', async () => {
    // What a cancelled sign-in sends back.
    withPendingAttempt('test-state');

    renderRouter('src/app', {
      initialUrl: '/auth/callback?error=access_denied&state=test-state',
    });

    expect(
      await screen.findByRole('heading', { name: 'Sign-in could not be completed' }),
    ).toBeTruthy();
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it('reports a rejected exchange', async () => {
    withPendingAttempt('test-state');
    globalThis.fetch = jest.fn(
      routeFetch({ '/oauth2/token': () => tokenEndpointError('invalid_grant') }),
    ) as unknown as typeof fetch;

    renderRouter('src/app', { initialUrl: '/auth/callback?code=test-code&state=test-state' });

    expect(
      await screen.findByRole('heading', { name: 'Sign-in could not be completed' }),
    ).toBeTruthy();
  });

  it('clears the pending attempt whatever the outcome', async () => {
    // The `state` and verifier are single-use: leaving either behind is what
    // makes a callback replayable.
    withPendingAttempt('the-real-state');

    renderRouter('src/app', { initialUrl: '/auth/callback?code=test-code&state=wrong-state' });
    await screen.findByRole('heading', { name: 'Sign-in could not be completed' });

    expect(browser.sessionStorage.entries.size).toBe(0);
  });

  it('offers a way back to sign-in after a failure', async () => {
    withPendingAttempt('the-real-state');

    const router = renderRouter('src/app', {
      initialUrl: '/auth/callback?code=test-code&state=wrong-state',
    });
    await screen.findByRole('heading', { name: 'Sign-in could not be completed' });

    await userEvent.press(screen.getByRole('button', { name: 'Back to sign in' }));

    // `getPathname()` rather than the `toHavePathname` matcher: the matcher is
    // registered at runtime but expo-router ships no type declaration for it,
    // so it would not typecheck.
    expect(router.getPathname()).toBe('/sign-in');
    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeTruthy();
  });
});
