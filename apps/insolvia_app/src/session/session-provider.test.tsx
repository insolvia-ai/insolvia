import { act, render, screen } from '@testing-library/react-native';
import { Text } from 'react-native';

import { readRefreshToken, SessionProvider, useSession, writeRefreshToken } from '@/session';
import type { CompleteSignInResult } from '@/session';
import {
  fakeJwt,
  installFakeBrowser,
  TEST_AUTH_CONFIG,
  TEST_EMAIL,
  tokenEndpointError,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

/**
 * A probe that renders the session as text and exposes its callbacks.
 *
 * Rendering the real provider rather than testing a reducer in isolation is
 * deliberate: the behaviours that matter here — bootstrap on load, refresh
 * dedupe, clearing on failure — are all effects and refs, and a test that
 * bypassed React would not exercise any of them.
 */
let latest: ReturnType<typeof useSession> | null = null;

function Probe() {
  const session = useSession();
  latest = session;
  return (
    <>
      <Text>{`status:${session.status}`}</Text>
      <Text>{`email:${session.user?.email ?? 'none'}`}</Text>
      <Text>{`configured:${String(session.isConfigured)}`}</Text>
    </>
  );
}

function renderSession(options: { configured?: boolean } = {}) {
  const configured = options.configured ?? true;
  return render(
    <SessionProvider config={configured ? TEST_AUTH_CONFIG : null}>
      <Probe />
    </SessionProvider>,
  );
}

/** The session as it stands right now. Never null after a render. */
function session() {
  if (latest === null) {
    throw new Error('the probe has not rendered');
  }
  return latest;
}

describe('the session', () => {
  let browser: FakeBrowser;
  let fetchMock: jest.Mock;
  const realFetch = globalThis.fetch;

  beforeEach(() => {
    latest = null;
    browser = installFakeBrowser();
    fetchMock = jest.fn().mockResolvedValue(tokenEndpointResponse());
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  afterEach(() => {
    browser.restore();
    globalThis.fetch = realFetch;
  });

  describe('bootstrapping on load', () => {
    it('restores the session from a stored refresh token', async () => {
      // The reload case: tokens are memory-only, so the refresh token in
      // localStorage is the ONLY thing that can carry a session across a page
      // load. Without this exchange, every reload would be a full sign-in.
      writeRefreshToken('stored-refresh-token');

      renderSession();

      expect(await screen.findByText('status:signed-in')).toBeTruthy();
      expect(screen.getByText(`email:${TEST_EMAIL}`)).toBeTruthy();

      const [url, init] = fetchMock.mock.calls[0] ?? [];
      expect(url).toBe(`${TEST_AUTH_CONFIG.domain}/oauth2/token`);
      expect(String(init?.body)).toContain('grant_type=refresh_token');
    });

    it('persists the rotated replacement token', async () => {
      // Rotation retires the token just used. Keeping the old one would sign
      // the user out at the next reload.
      writeRefreshToken('stored-refresh-token');
      fetchMock.mockResolvedValue(tokenEndpointResponse({ refreshToken: 'rotated-refresh-token' }));

      renderSession();
      await screen.findByText('status:signed-in');

      expect(readRefreshToken()).toBe('rotated-refresh-token');
    });

    it('keeps the existing token when a response does not rotate', async () => {
      writeRefreshToken('stored-refresh-token');
      fetchMock.mockResolvedValue(tokenEndpointResponse({ refreshToken: null }));

      renderSession();
      await screen.findByText('status:signed-in');

      expect(readRefreshToken()).toBe('stored-refresh-token');
    });

    it('settles on signed-out with no request when nothing is stored', async () => {
      renderSession();

      expect(await screen.findByText('status:signed-out')).toBeTruthy();
      expect(fetchMock).not.toHaveBeenCalled();
    });

    it('settles on signed-out with no request when there is no hosted UI', async () => {
      writeRefreshToken('stored-refresh-token');

      renderSession({ configured: false });

      expect(await screen.findByText('status:signed-out')).toBeTruthy();
      expect(screen.getByText('configured:false')).toBeTruthy();
      expect(fetchMock).not.toHaveBeenCalled();
    });

    it('clears the stored token when the refresh fails', async () => {
      // Expired, revoked, or rotated away. Keeping a dead token would retry it
      // on every reload forever.
      writeRefreshToken('retired-refresh-token');
      fetchMock.mockResolvedValue(tokenEndpointError('invalid_grant'));

      renderSession();

      expect(await screen.findByText('status:signed-out')).toBeTruthy();
      expect(readRefreshToken()).toBeNull();
      expect(browser.localStorage.entries.size).toBe(0);
    });

    it('clears the session when the token endpoint is unreachable', async () => {
      writeRefreshToken('stored-refresh-token');
      fetchMock.mockRejectedValue(new TypeError('network request failed'));

      renderSession();

      expect(await screen.findByText('status:signed-out')).toBeTruthy();
      expect(readRefreshToken()).toBeNull();
    });
  });

  describe('access tokens', () => {
    it('hands out the in-memory token without a network call', async () => {
      writeRefreshToken('stored-refresh-token');
      fetchMock.mockResolvedValue(
        tokenEndpointResponse({ accessToken: 'fresh-access-token', expiresIn: 3600 }),
      );

      renderSession();
      await screen.findByText('status:signed-in');
      fetchMock.mockClear();

      await act(async () => {
        await expect(session().accessToken()).resolves.toBe('fresh-access-token');
      });
      expect(fetchMock).not.toHaveBeenCalled();
    });

    it('refreshes proactively when the token is inside the expiry skew', async () => {
      // The pool issues one-hour access tokens; a 10-second one is well inside
      // the 60-second skew, so the very next call must renew rather than send a
      // credential that dies in transit.
      writeRefreshToken('stored-refresh-token');
      fetchMock.mockResolvedValue(
        tokenEndpointResponse({ accessToken: 'nearly-expired', expiresIn: 10 }),
      );

      renderSession();
      await screen.findByText('status:signed-in');

      fetchMock.mockResolvedValue(
        tokenEndpointResponse({ accessToken: 'renewed-access-token', expiresIn: 3600 }),
      );
      await act(async () => {
        await expect(session().accessToken()).resolves.toBe('renewed-access-token');
      });
      expect(fetchMock).toHaveBeenCalled();
    });

    it('yields undefined when there is no session', async () => {
      renderSession();
      await screen.findByText('status:signed-out');

      await act(async () => {
        await expect(session().accessToken()).resolves.toBeUndefined();
      });
    });

    it('shares one request between concurrent refreshes', async () => {
      // With rotation on, a second simultaneous refresh would present a token
      // the first had already retired.
      writeRefreshToken('stored-refresh-token');
      fetchMock.mockResolvedValue(tokenEndpointResponse({ expiresIn: 3600 }));

      renderSession();
      await screen.findByText('status:signed-in');
      fetchMock.mockClear();

      await act(async () => {
        await Promise.all([session().refresh(), session().refresh(), session().refresh()]);
      });

      expect(fetchMock).toHaveBeenCalledTimes(1);
    });
  });

  describe('completing the redirect back from the hosted UI', () => {
    async function beginSignIn(returnTo: string | null = null) {
      renderSession();
      await screen.findByText('status:signed-out');
      await act(async () => {
        await session().signIn(returnTo);
      });
      // The authorize URL the app just left for, so a test can read the `state`
      // it generated rather than reaching into storage.
      const authorizeUrl = browser.navigations.at(-1) ?? '';
      const state = new RegExp('[?&]state=([^&]+)').exec(authorizeUrl)?.[1] ?? '';
      return { authorizeUrl, state: decodeURIComponent(state) };
    }

    it('exchanges the code and signs in', async () => {
      const { state } = await beginSignIn();
      fetchMock.mockResolvedValue(
        tokenEndpointResponse({ idToken: fakeJwt({ email: TEST_EMAIL }) }),
      );

      let result: CompleteSignInResult | null = null;
      await act(async () => {
        result = await session().completeSignIn({ code: 'test-code', state });
      });

      expect(result).toEqual({ ok: true, returnTo: '/' });
      expect(await screen.findByText('status:signed-in')).toBeTruthy();
      expect(screen.getByText(`email:${TEST_EMAIL}`)).toBeTruthy();
    });

    it('sends the authorization-code grant with the stored verifier', async () => {
      const { state } = await beginSignIn();
      fetchMock.mockClear();
      fetchMock.mockResolvedValue(tokenEndpointResponse());

      await act(async () => {
        await session().completeSignIn({ code: 'test-code', state });
      });

      const body = String(fetchMock.mock.calls[0]?.[1]?.body ?? '');
      expect(body).toContain('grant_type=authorization_code');
      expect(body).toContain('code=test-code');
      expect(body).toContain('code_verifier=');
    });

    it('returns the user to where they were headed', async () => {
      const { state } = await beginSignIn('/somewhere');

      let result: CompleteSignInResult | null = null;
      await act(async () => {
        result = await session().completeSignIn({ code: 'test-code', state });
      });

      expect(result).toEqual({ ok: true, returnTo: '/somewhere' });
    });

    it.each([
      ['an absolute URL', 'https://evil.example.test/phish'],
      ['a protocol-relative URL', '//evil.example.test/phish'],
      ['a bare host', 'evil.example.test'],
    ])('refuses to follow %s after signing in', async (_label, hostile) => {
      // An open redirect fired immediately after a successful sign-in is the
      // most credible phishing moment there is.
      const { state } = await beginSignIn(hostile);

      let result: CompleteSignInResult | null = null;
      await act(async () => {
        result = await session().completeSignIn({ code: 'test-code', state });
      });

      expect(result).toEqual({ ok: true, returnTo: '/' });
    });

    it('rejects a mismatched state', async () => {
      // RFC 6749 section 10.12: a code arriving with the wrong `state` was not
      // requested by this browser. An error, never a silent retry.
      await beginSignIn();
      fetchMock.mockClear();

      let result: CompleteSignInResult | null = null;
      await act(async () => {
        result = await session().completeSignIn({ code: 'test-code', state: 'not-the-state' });
      });

      expect(result).toMatchObject({ ok: false });
      expect(screen.getByText('status:signed-out')).toBeTruthy();
      // No exchange was attempted at all.
      expect(fetchMock).not.toHaveBeenCalled();
    });

    it('rejects a callback with no stored attempt behind it', async () => {
      renderSession();
      await screen.findByText('status:signed-out');

      let result: CompleteSignInResult | null = null;
      await act(async () => {
        result = await session().completeSignIn({ code: 'test-code', state: 'any-state' });
      });

      expect(result).toMatchObject({ ok: false });
      expect(fetchMock).not.toHaveBeenCalled();
    });

    it('rejects a callback missing its code', async () => {
      const { state } = await beginSignIn();
      fetchMock.mockClear();

      let result: CompleteSignInResult | null = null;
      await act(async () => {
        result = await session().completeSignIn({ state });
      });

      expect(result).toMatchObject({ ok: false });
      expect(fetchMock).not.toHaveBeenCalled();
    });

    it('reports the error the hosted UI sent instead of exchanging', async () => {
      const { state } = await beginSignIn();
      fetchMock.mockClear();

      let result: CompleteSignInResult | null = null;
      await act(async () => {
        result = await session().completeSignIn({ error: 'access_denied', state });
      });

      expect(result).toMatchObject({ ok: false });
      expect(fetchMock).not.toHaveBeenCalled();
    });

    it.each([
      ['succeeds', () => tokenEndpointResponse()],
      ['fails', () => tokenEndpointError('invalid_grant')],
    ])('clears the stored state and verifier when the exchange %s', async (_label, response) => {
      // Single-use by definition: a `state` left behind is replayable, and a
      // leftover verifier would let an unrelated second callback appear to
      // validate.
      const { state } = await beginSignIn();
      fetchMock.mockResolvedValue(response());

      await act(async () => {
        await session().completeSignIn({ code: 'test-code', state });
      });

      expect(browser.sessionStorage.entries.size).toBe(0);
    });

    it('reports failure when the exchange itself is rejected', async () => {
      const { state } = await beginSignIn();
      fetchMock.mockResolvedValue(tokenEndpointError('invalid_grant'));

      let result: CompleteSignInResult | null = null;
      await act(async () => {
        result = await session().completeSignIn({ code: 'test-code', state });
      });

      expect(result).toMatchObject({ ok: false });
      expect(screen.getByText('status:signed-out')).toBeTruthy();
    });
  });

  describe('starting sign-in', () => {
    it('stores the attempt and leaves for the hosted UI with PKCE', async () => {
      renderSession();
      await screen.findByText('status:signed-out');

      await act(async () => {
        await session().signIn(null);
      });

      const [url = ''] = browser.navigations;
      expect(url.startsWith(`${TEST_AUTH_CONFIG.domain}/oauth2/authorize?`)).toBe(true);
      expect(url).toContain('code_challenge_method=S256');
      expect(url).toContain('code_challenge=');
      // Written BEFORE the redirect — once the browser leaves, the module is
      // gone and the verifier has to be waiting when it returns.
      expect(browser.sessionStorage.entries.size).toBe(1);
    });

    it('does not send the verifier itself to the authorize endpoint', async () => {
      renderSession();
      await screen.findByText('status:signed-out');

      await act(async () => {
        await session().signIn(null);
      });

      const stored = browser.sessionStorage.entries.values().next().value ?? '';
      const verifier = (JSON.parse(stored) as { codeVerifier: string }).codeVerifier;
      expect(browser.navigations[0] ?? '').not.toContain(verifier);
    });

    it('refuses and explains when no hosted UI is configured', async () => {
      renderSession({ configured: false });
      await screen.findByText('status:signed-out');

      await act(async () => {
        await session().signIn(null);
      });

      expect(browser.navigations).toHaveLength(0);
      expect(session().error).toBe('Sign-in is not configured for this environment.');
    });
  });

  describe('signing out', () => {
    it('does both legs — clears everything, then redirects to /logout', async () => {
      // ADR 0007: dropping the local tokens alone leaves Cognito's hosted-UI
      // session cookie intact, so the next sign-in silently re-authenticates
      // the same user. On a shared machine that reads, correctly, as
      // "sign-out did not work".
      writeRefreshToken('stored-refresh-token');
      renderSession();
      await screen.findByText('status:signed-in');

      await act(() => {
        session().signOut();
      });

      expect(await screen.findByText('status:signed-out')).toBeTruthy();
      expect(screen.getByText('email:none')).toBeTruthy();

      // Leg one: nothing left in any store.
      expect(readRefreshToken()).toBeNull();
      expect(browser.localStorage.entries.size).toBe(0);
      expect(browser.sessionStorage.entries.size).toBe(0);

      // Leg two: the hosted UI's logout endpoint, with logout_uri as the origin.
      const url = browser.navigations.at(-1) ?? '';
      expect(url.startsWith(`${TEST_AUTH_CONFIG.domain}/logout?`)).toBe(true);
      expect(url).toContain(`client_id=${TEST_AUTH_CONFIG.clientId}`);
      expect(url).toContain('logout_uri=');
    });

    it('is safe to call when already signed out', async () => {
      renderSession();
      await screen.findByText('status:signed-out');

      expect(() => {
        act(() => {
          session().signOut();
        });
      }).not.toThrow();
    });
  });

  describe('a component outside the provider', () => {
    it('fails loudly instead of pretending to be signed out', () => {
      // A signed-out default would send the user to sign-in in a loop and look
      // like a product bug rather than the wiring bug it is.
      const consoleError = jest.spyOn(console, 'error').mockImplementation(() => undefined);
      try {
        expect(() => render(<Probe />)).toThrow(
          'useSession must be used inside a <SessionProvider>',
        );
      } finally {
        consoleError.mockRestore();
      }
    });
  });
});
