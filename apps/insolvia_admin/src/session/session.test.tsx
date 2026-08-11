/**
 * The memory-only session, pinned.
 *
 * What matters here is what the session refuses: a credential with no expiry
 * never becomes a session, an expiring token stops being presented a minute
 * early, and nothing ever touches storage — the assertions a future
 * "convenience" refresh token or localStorage cache would have to delete.
 */

import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import type { ReactNode } from 'react';

import { SessionProvider, useSession } from './session';

function fakeJwt(claims: Record<string, unknown>): string {
  const encode = (value: unknown) =>
    btoa(JSON.stringify(value)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${encode({ alg: 'RS256', typ: 'JWT' })}.${encode(claims)}.fake-signature`;
}

function wrapper({ children }: { children: ReactNode }) {
  return <SessionProvider>{children}</SessionProvider>;
}

// Without vitest `globals`, testing-library cannot register its automatic
// cleanup.
afterEach(cleanup);

describe('the staff session', () => {
  it('accepts a credential and serves it, with the email surfaced', () => {
    const credential = fakeJwt({
      email: 'operator@insolvia.ai',
      exp: Math.floor(Date.now() / 1000) + 3600,
    });
    const { result } = renderHook(() => useSession(), { wrapper });

    act(() => result.current.acceptCredential(credential));

    expect(result.current.signedIn).toBe(true);
    expect(result.current.email).toBe('operator@insolvia.ai');
    expect(result.current.token()).toBe(credential);
  });

  it('stops presenting a token inside its final minute', () => {
    // 30s of life left is less than the 60s margin: a request in flight when
    // it lapses would 401 anyway, so the session reports signed-out up front.
    const credential = fakeJwt({ exp: Math.floor(Date.now() / 1000) + 30 });
    const { result } = renderHook(() => useSession(), { wrapper });

    act(() => result.current.acceptCredential(credential));

    expect(result.current.token()).toBeNull();
  });

  it('refuses a credential without an expiry, keeping no session', () => {
    const { result } = renderHook(() => useSession(), { wrapper });

    expect(() =>
      act(() => result.current.acceptCredential(fakeJwt({ email: 'x@insolvia.ai' }))),
    ).toThrow(/expiry/);
    expect(result.current.signedIn).toBe(false);
    expect(result.current.token()).toBeNull();
  });

  it('refuses a credential that is not a JWT at all', () => {
    const { result } = renderHook(() => useSession(), { wrapper });

    expect(() => act(() => result.current.acceptCredential('not-a-jwt'))).toThrow();
  });

  it('signs out completely', () => {
    const credential = fakeJwt({
      email: 'operator@insolvia.ai',
      exp: Math.floor(Date.now() / 1000) + 3600,
    });
    const { result } = renderHook(() => useSession(), { wrapper });
    act(() => result.current.acceptCredential(credential));

    act(() => result.current.signOut());

    expect(result.current.signedIn).toBe(false);
    expect(result.current.email).toBeNull();
    expect(result.current.token()).toBeNull();
  });

  it('touches no storage — tokens are memory-only, and no handshake remains', () => {
    // The PKCE-era handshake kept state/verifier in sessionStorage; the GIS
    // flow needs nothing there, so anything appearing is a regression toward
    // exactly the persistence #209 decided against.
    const credential = fakeJwt({ exp: Math.floor(Date.now() / 1000) + 3600 });
    const { result } = renderHook(() => useSession(), { wrapper });

    act(() => result.current.acceptCredential(credential));

    expect(sessionStorage.length).toBe(0);
  });
});
