import { act, render, screen } from '@testing-library/react-native';
import { Text } from 'react-native';

import {
  PortalSessionProvider,
  safePortalReturnTo,
  usePortalSession,
} from '@/session/portal-session-provider';
import type { CompleteSignInResult } from '@/session';
import {
  fakeJwt,
  installFakeBrowser,
  TEST_ORIGIN,
  tokenEndpointError,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

/**
 * The client portal's session (ADR 0023). What these tests exist to hold is
 * the one property that makes it a second provider rather than a flag on
 * the first: NOTHING a sign-in yields is written anywhere but memory.
 */

const PORTAL_CONFIG = {
  domain: 'https://insolvia-test.auth.example.test',
  clientId: 'test-portal-client-id-000',
};
const CLIENT_EMAIL = 'client@example.test';

let latest: ReturnType<typeof usePortalSession> | null = null;

function Probe() {
  const session = usePortalSession();
  latest = session;
  return (
    <>
      <Text>{`status:${session.status}`}</Text>
      <Text>{`email:${session.user?.email ?? 'none'}`}</Text>
      <Text>{`configured:${String(session.isConfigured)}`}</Text>
    </>
  );
}

function session() {
  if (latest === null) throw new Error('the probe has not rendered');
  return latest;
}

function renderPortal(config: typeof PORTAL_CONFIG | null = PORTAL_CONFIG) {
  return render(
    <PortalSessionProvider config={config}>
      <Probe />
    </PortalSessionProvider>,
  );
}

describe('the portal session', () => {
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

  async function signedIn(returnTo: string | null = null): Promise<CompleteSignInResult> {
    renderPortal();
    await screen.findByText('status:signed-out');
    await act(async () => {
      await session().signIn(returnTo);
    });
    const authorize = browser.navigations.at(-1) ?? '';
    const state = decodeURIComponent(/[?&]state=([^&]+)/.exec(authorize)?.[1] ?? '');
    fetchMock.mockResolvedValue(
      tokenEndpointResponse({ idToken: fakeJwt({ email: CLIENT_EMAIL }) }),
    );
    let result: CompleteSignInResult | null = null;
    await act(async () => {
      result = await session().completeSignIn({ code: 'test-code', state });
    });
    if (result === null) throw new Error('completeSignIn did not resolve');
    return result;
  }

  it('starts signed out on every load, even with a staff session stored', async () => {
    // A staff refresh token on the same origin is the STAFF session's, and
    // must never become a debtor's portal session.
    browser.localStorage.setItem('insolvia.auth.refresh-token', 'staff-refresh-token');

    renderPortal();

    expect(await screen.findByText('status:signed-out')).toBeTruthy();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('leaves for the PORTAL client, PKCE and all, returning to the portal callback', async () => {
    renderPortal();
    await screen.findByText('status:signed-out');

    await act(async () => {
      await session().signIn('/portal');
    });

    const authorize = new URL(browser.navigations.at(-1) ?? '');
    expect(authorize.origin + authorize.pathname).toBe(`${PORTAL_CONFIG.domain}/oauth2/authorize`);
    expect(authorize.searchParams.get('client_id')).toBe(PORTAL_CONFIG.clientId);
    expect(authorize.searchParams.get('redirect_uri')).toBe(`${TEST_ORIGIN}/portal/auth/callback`);
    expect(authorize.searchParams.get('code_challenge_method')).toBe('S256');
    expect(authorize.searchParams.get('code_challenge')).not.toBeNull();
  });

  it('signs in with the code, and writes no token anywhere but memory', async () => {
    const result = await signedIn();

    expect(result).toEqual({ ok: true, returnTo: '/portal' });
    expect(await screen.findByText('status:signed-in')).toBeTruthy();
    expect(screen.getByText(`email:${CLIENT_EMAIL}`)).toBeTruthy();
    // THE property: after a whole sign-in, localStorage is exactly as it was
    // (empty), and the pending attempt has been cleared from sessionStorage.
    expect([...browser.localStorage.entries.keys()]).toEqual([]);
    expect([...browser.sessionStorage.entries.keys()]).toEqual([]);
    await act(async () => {
      await expect(session().accessToken()).resolves.toBe('test-access-token');
    });
  });

  it('refreshes from the in-memory refresh token, and still writes nothing', async () => {
    await signedIn();
    fetchMock.mockClear();
    fetchMock.mockResolvedValue(tokenEndpointResponse({ accessToken: 'renewed-access-token' }));

    let survived = false;
    await act(async () => {
      survived = await session().refresh();
    });

    expect(survived).toBe(true);
    expect(String(fetchMock.mock.calls[0]?.[1]?.body ?? '')).toContain(
      'refresh_token=test-refresh-token',
    );
    expect([...browser.localStorage.entries.keys()]).toEqual([]);
    await act(async () => {
      await expect(session().accessToken()).resolves.toBe('renewed-access-token');
    });
  });

  it('a refused refresh ends the session', async () => {
    await signedIn();
    fetchMock.mockResolvedValue(tokenEndpointError('invalid_grant'));

    await act(async () => {
      await session().refresh();
    });

    expect(await screen.findByText('status:signed-out')).toBeTruthy();
  });

  it('refuses a callback whose state it did not issue', async () => {
    renderPortal();
    await screen.findByText('status:signed-out');
    await act(async () => {
      await session().signIn(null);
    });

    let result: CompleteSignInResult | null = null;
    await act(async () => {
      result = await session().completeSignIn({ code: 'test-code', state: 'not-the-state' });
    });

    expect(result).toEqual({
      ok: false,
      message: 'This sign-in could not be verified. Start again.',
    });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('does not accept a STAFF sign-in attempt as its own', async () => {
    // The staff session's pending attempt lives under its own key; a portal
    // callback must not consume it.
    browser.sessionStorage.setItem(
      'insolvia.auth.pending-authorization',
      JSON.stringify({ state: 'staff-state', codeVerifier: 'staff-verifier', returnTo: null }),
    );
    renderPortal();
    await screen.findByText('status:signed-out');

    let result: CompleteSignInResult | null = null;
    await act(async () => {
      result = await session().completeSignIn({ code: 'test-code', state: 'staff-state' });
    });

    expect(result).toMatchObject({ ok: false });
    expect(browser.sessionStorage.entries.has('insolvia.auth.pending-authorization')).toBe(true);
  });

  it('signs out of both halves, landing on the portal', async () => {
    await signedIn();

    act(() => {
      session().signOut();
    });

    expect(await screen.findByText('status:signed-out')).toBeTruthy();
    const logout = new URL(browser.navigations.at(-1) ?? '');
    expect(logout.pathname).toBe('/logout');
    expect(logout.searchParams.get('client_id')).toBe(PORTAL_CONFIG.clientId);
    expect(logout.searchParams.get('logout_uri')).toBe(`${TEST_ORIGIN}/portal`);
    await act(async () => {
      await expect(session().accessToken()).resolves.toBeUndefined();
    });
  });

  it('says so when this build has no portal client', async () => {
    renderPortal(null);
    expect(await screen.findByText('configured:false')).toBeTruthy();

    await act(async () => {
      await session().signIn(null);
    });

    expect(session().error).toBe('Sign-in is not configured for this environment.');
    expect(browser.navigations).toEqual([]);
  });

  it('throws outside its provider rather than reading as signed out', () => {
    jest.spyOn(console, 'error').mockImplementation(() => {});
    expect(() => render(<Probe />)).toThrow(/PortalSessionProvider/);
  });
});

describe('safePortalReturnTo', () => {
  it.each([
    ['/portal', '/portal'],
    ['/portal/anything', '/portal/anything'],
    ['/', '/portal'],
    ['/cases/abc', '/portal'],
    ['/portalish', '/portal'],
    ['//evil.example.test/portal', '/portal'],
    ['https://evil.example.test/portal', '/portal'],
    ['/portal/auth/callback?code=x', '/portal'],
    [null, '/portal'],
  ])('%s → %s', (candidate, expected) => {
    expect(safePortalReturnTo(candidate)).toBe(expected);
  });
});
