import { waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { readRefreshToken, writeRefreshToken } from '@/session';
import {
  installFakeBrowser,
  jsonResponse,
  principalResponse,
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

/**
 * The `GET /v1/me` panel's 401 policy — the reactive half of ADR 0007's refresh
 * rule, and the part with a rule that is easy to get subtly wrong.
 *
 * Each test scripts the two endpoints call-by-call, because the interesting
 * thing here is the **order and count** of requests, not their content: exactly
 * one refresh, exactly one retry, and no loop.
 *
 * They settle on the SCRIPT rather than on rendered claims. The claims used to
 * be on the home screen, which made them a convenient signal; MePanel now sits
 * collapsed at the bottom of /account, so the panel's own rendering is tested
 * where it lives and this file asserts what its name says it asserts.
 */
describe('the API session panel', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  /** Serves each URL from its own queue, so a test can script a sequence. */
  function scriptFetch(script: { token?: (() => Response)[]; me?: (() => Response)[] }) {
    const token = [...(script.token ?? [])];
    const me = [...(script.me ?? [])];
    return jest.fn((url: string) => {
      const queue = url.includes('/oauth2/token') ? token : url.includes('/v1/me') ? me : null;
      if (queue === null) {
        return Promise.reject(new Error(`unexpected request to ${url}`));
      }
      const next = queue.shift();
      if (next === undefined) {
        return Promise.reject(new Error(`no scripted response left for ${url}`));
      }
      return Promise.resolve(next());
    });
  }

  function useFetch(mock: jest.Mock) {
    globalThis.fetch = mock as unknown as typeof fetch;
    return mock;
  }

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    browser.restore();
    globalThis.fetch = realFetch;
  });

  it('asks once, and does not refresh when nothing was rejected', async () => {
    const fetchMock = useFetch(
      scriptFetch({ token: [() => tokenEndpointResponse()], me: [() => principalResponse()] }),
    );

    renderRouter('src/app', { initialUrl: '/' });

    await waitFor(() => {
      expect(fetchMock.mock.calls.filter(([url]) => String(url).includes('/v1/me'))).toHaveLength(
        1,
      );
    });
    // The bootstrap refresh, and nothing reactive on top of it.
    expect(
      fetchMock.mock.calls.filter(([url]) => String(url).includes('/oauth2/token')),
    ).toHaveLength(1);
  });

  it('refreshes once and retries when the API answers 401', async () => {
    // A server 401 means the token was rejected — worth exactly one refresh.
    const fetchMock = useFetch(
      scriptFetch({
        token: [
          () => tokenEndpointResponse({ accessToken: 'first-access-token' }),
          () => tokenEndpointResponse({ accessToken: 'second-access-token' }),
        ],
        me: [
          () => jsonResponse(401, { error: 'Unauthorized', message: 'token expired' }),
          () => principalResponse(),
        ],
      }),
    );

    renderRouter('src/app', { initialUrl: '/' });

    await waitFor(() => {
      expect(fetchMock.mock.calls.filter(([url]) => String(url).includes('/v1/me'))).toHaveLength(
        2,
      );
    });

    const meCalls = fetchMock.mock.calls.filter(([url]) => String(url).includes('/v1/me'));
    const tokenCalls = fetchMock.mock.calls.filter(([url]) =>
      String(url).includes('/oauth2/token'),
    );
    // One bootstrap refresh plus exactly one reactive refresh — never a loop.
    expect(tokenCalls).toHaveLength(2);
    expect(meCalls).toHaveLength(2);
    // The retry carried the NEW token, which is the whole point of refreshing.
    const retryHeaders = meCalls[1]?.[1]?.headers as Record<string, string> | undefined;
    expect(retryHeaders?.Authorization).toBe('Bearer second-access-token');
  });

  it('signs out — both legs — when the refresh after a 401 fails', async () => {
    // Nothing recoverable is left: the refresh token is retired or revoked.
    useFetch(
      scriptFetch({
        token: [() => tokenEndpointResponse(), () => tokenEndpointError('invalid_grant')],
        me: [() => jsonResponse(401, { error: 'Unauthorized', message: 'token expired' })],
      }),
    );

    renderRouter('src/app', { initialUrl: '/' });

    await waitFor(() => {
      expect(readRefreshToken()).toBeNull();
    });
    // Leg two: Cognito's own session ends as well.
    await waitFor(() => {
      expect(browser.navigations.at(-1) ?? '').toContain('/logout?');
    });
  });

  it('signs out when the retry after a successful refresh also 401s', async () => {
    // The one-retry budget is spent. A second refresh would present a token
    // rotation had already retired.
    useFetch(
      scriptFetch({
        token: [() => tokenEndpointResponse(), () => tokenEndpointResponse()],
        me: [
          () => jsonResponse(401, { error: 'Unauthorized', message: 'nope' }),
          () => jsonResponse(401, { error: 'Unauthorized', message: 'still nope' }),
        ],
      }),
    );

    renderRouter('src/app', { initialUrl: '/' });

    await waitFor(() => {
      expect(browser.navigations.at(-1) ?? '').toContain('/logout?');
    });
    expect(readRefreshToken()).toBeNull();
  });

  it('reports a non-401 failure without touching the session', async () => {
    // A 500 is the API's problem, not the session's. Signing the user out
    // over it would be a spectacular overreaction.
    useFetch(
      scriptFetch({
        token: [() => tokenEndpointResponse()],
        me: [() => jsonResponse(500, { error: 'InternalServerError' })],
      }),
    );

    const fetchMock = globalThis.fetch as unknown as jest.Mock;
    renderRouter('src/app', { initialUrl: '/' });

    await waitFor(() => {
      expect(fetchMock.mock.calls.filter(([url]) => String(url).includes('/v1/me'))).toHaveLength(
        1,
      );
    });

    // NO SECOND TOKEN CALL: a 500 is the API's problem, and refreshing over it
    // would be treating a server fault as an expired credential.
    expect(
      fetchMock.mock.calls.filter(([url]) => String(url).includes('/oauth2/token')),
    ).toHaveLength(1);
    // The session is intact: still a stored token (rotated by the bootstrap
    // refresh, hence "not null" rather than a literal), and no sign-out.
    expect(readRefreshToken()).not.toBeNull();
    expect(browser.navigations).toHaveLength(0);
  });
});
