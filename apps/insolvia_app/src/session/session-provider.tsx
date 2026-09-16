import type { Href } from 'expo-router';
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import type { ReactNode } from 'react';

import { resolveAuthConfig } from '@/config/environment';
import type { AuthConfig } from '@/config/environment';
import { currentOrigin, navigateTo } from '@/platform/browser';
import { readIdTokenClaims } from '@/session/id-token';
import {
  authorizeUrl,
  callbackUrlFor,
  exchangeCodeForTokens,
  logoutUrl,
  refreshTokens,
} from '@/session/oauth';
import type { TokenSet } from '@/session/oauth';
import { createPkcePair, randomUrlSafeToken } from '@/session/pkce';
import {
  clearPendingAuthorization,
  clearRefreshToken,
  readPendingAuthorization,
  readRefreshToken,
  writePendingAuthorization,
  writeRefreshToken,
} from '@/session/token-store';

/**
 * The signed-in session, and the app's **first React context**.
 *
 * There was no context convention here before this file, so it sets one, and
 * the shape is worth stating because the next context should copy it:
 *
 * - **One provider, mounted once, in `src/app/_layout.tsx`.** It wraps the
 *   navigator so every route is inside it.
 * - **The value is a single `useMemo`'d object** of stable callbacks. Anything
 *   that changes identity every render would re-run every consumer's effects —
 *   `MePanel` builds an API client from `accessToken`, and a new function each
 *   render would refetch forever.
 * - **`useSession()` throws when there is no provider**, rather than handing
 *   back a plausible signed-out default. A missing provider is a wiring bug
 *   that should fail loudly at the first render, not present as "signed out"
 *   and send the user to sign-in in a loop.
 * - **Mutable token material lives in a ref, not in state.** Tokens are not
 *   render inputs — `status` and `user` are — and putting them in state would
 *   put a bearer credential into React's fiber tree and every DevTools session.
 *
 * ## The state machine
 *
 * ```text
 *   no stored refresh token          ┌─────────────┐
 *   ─────────────────────────────►   │ signed-out  │
 *                                    └──────┬──────┘
 *   stored refresh token                    │ signIn()
 *   ─────────────────────────────►          ▼
 *            ┌───────────┐   refresh fails / signOut()   hosted UI
 *            │ signed-in │ ─────────────────────────►  /oauth2/authorize
 *            └───────────┘                                  │
 *                  ▲                                        ▼
 *                  └──────── completeSignIn() ◄──── /auth/callback
 * ```
 *
 * **There is no `loading` state, and the first render already knows which of
 * the two it is in.** Whether a refresh token is stored is a synchronous read,
 * so a reload with one starts as `signed-in` — optimistically, with
 * {@link SessionContextValue.restoring} set while the stored token is being
 * exchanged for an access token in the background — and a reload without one
 * starts as `signed-out` with no request made.
 *
 * The optimism is safe because of what `signed-in` does and does not unlock.
 * It lets the app's chrome and each screen's own loading skeleton render at
 * once. It does not let any protected DATA render: every API call goes through
 * {@link SessionContextValue.accessToken}, which waits for the in-flight
 * exchange, so nothing is fetched — and nothing case-shaped is shown — until
 * Cognito has actually vouched for the session. If the exchange fails the
 * status flips to `signed-out` and the route guard redirects, exactly as it
 * would have after a blocking check. What is gone is the blocking check
 * itself, and with it the "Checking your session" page every reload used to
 * paint (issue #78 wanted two things — never bounce a signed-in user to
 * sign-in, never flash case data at someone with no session — and both hold
 * without it).
 */
export type SessionStatus = 'signed-in' | 'signed-out';

/** Display identity, read from the ID token. Never used for authorization. */
export interface SessionUser {
  /** The address to show in the UI. `null` if the token carried no claim. */
  readonly email: string | null;
  /** The `sub` claim — a stable Cognito user id, not an email. */
  readonly subject: string | null;
}

/** The outcome of handling the hosted UI's redirect back to `/auth/callback`. */
export type CompleteSignInResult =
  { readonly ok: true; readonly returnTo: Href } | { readonly ok: false; readonly message: string };

/** The query parameters the hosted UI sends back. All optional; all untrusted. */
export interface CallbackParams {
  readonly code?: string | undefined;
  readonly state?: string | undefined;
  readonly error?: string | undefined;
}

/** The session context's public API. */
export interface SessionContextValue {
  /** Where the session is. See the diagram above. */
  readonly status: SessionStatus;

  /**
   * `true` from a reload with a stored refresh token until that token has been
   * exchanged — the window in which {@link status} is `signed-in` on trust and
   * {@link user} is still `null`. It ends in `signed-in` with a `user`, or in
   * `signed-out`. Screens rarely need it: a screen that fetches its data waits
   * on {@link accessToken} implicitly. The sign-in screen is the one caller,
   * so it does not bounce a user onward and then straight back.
   */
  readonly restoring: boolean;

  /** Display identity while {@link status} is `signed-in`, else `null`. */
  readonly user: SessionUser | null;

  /**
   * Whether this build has a hosted UI to sign in against.
   *
   * `false` on a `local` build with no `EXPO_PUBLIC_COGNITO_*` variables, which
   * is a supported state: the sign-in screen says so instead of building a
   * broken redirect.
   */
  readonly isConfigured: boolean;

  /** A message for a sign-in that could not be *started*, else `null`. */
  readonly error: string | null;

  /**
   * Leaves for the hosted UI. `returnTo` is the in-app path to land on
   * afterwards; it survives the round trip in `sessionStorage`.
   */
  signIn(returnTo?: string | null): Promise<void>;

  /**
   * Both legs, always (ADR 0007): clears the in-memory tokens **and** the
   * persisted refresh token, then redirects to the hosted UI's `/logout`.
   * Dropping the local tokens alone leaves Cognito's session cookie intact, so
   * the next sign-in silently re-authenticates the same user.
   */
  signOut(): void;

  /**
   * The access token for an API call, refreshed first if it is at or near
   * expiry. `undefined` when there is no session — the API client turns that
   * into a client-source `ApiUnauthorizedException` without a round trip.
   */
  accessToken(): Promise<string | undefined>;

  /**
   * Forces one refresh. `true` if the session survived.
   *
   * This is the reactive half of ADR 0007's refresh rule: a caller that got a
   * **server** 401 calls it once and retries. A failure clears the session
   * locally — it does not redirect, so the caller decides whether a full
   * sign-out is warranted.
   */
  refresh(): Promise<boolean>;

  /** Completes the authorization-code exchange. Called only by the callback screen. */
  completeSignIn(params: CallbackParams): Promise<CompleteSignInResult>;
}

/**
 * Refresh this far before the access token actually expires.
 *
 * 60 seconds covers clock skew between the browser and Cognito plus the
 * round trip, so a token is never sent to the API in the second it dies. The
 * pool issues one-hour access tokens, so the cost is at most one extra refresh
 * an hour.
 */
const EXPIRY_SKEW_MS = 60_000;

const SessionContext = createContext<SessionContextValue | null>(null);

export interface SessionProviderProps {
  children: ReactNode;
  /**
   * Overrides the build's hosted-UI configuration. Production code never passes
   * it; it exists so a test can mount a configured *or* an unconfigured
   * provider without touching the environment.
   */
  config?: AuthConfig | null;
}

/**
 * Provides the session to the whole app. Mounted once, in `src/app/_layout.tsx`.
 */
export function SessionProvider({ children, config: configOverride }: SessionProviderProps) {
  // Resolved once per mount: `resolveAuthConfig()` reads build-time constants,
  // so re-resolving on every render would only churn object identity and
  // invalidate the memo below.
  const [config] = useState<AuthConfig | null>(() => configOverride ?? resolveAuthConfig());

  /**
   * Decided on the first render, from storage, so no render ever has to show
   * a "not sure yet" state — see the header. `config === null` (no hosted UI)
   * short-circuits to `signed-out` without touching storage at all.
   */
  const [status, setStatus] = useState<SessionStatus>(() =>
    config !== null && readRefreshToken() !== null ? 'signed-in' : 'signed-out',
  );
  const [restoring, setRestoring] = useState<boolean>(() => status === 'signed-in');
  const [user, setUser] = useState<SessionUser | null>(null);
  const [error, setError] = useState<string | null>(null);

  /**
   * The access and ID tokens — **memory only, and never state.** This ref is
   * the whole of "tokens are not persisted": nothing reads it but the two
   * functions below, and nothing writes it to a store.
   */
  const tokensRef = useRef<TokenSet | null>(null);

  /**
   * The in-flight refresh, so concurrent callers share one request. Without it,
   * a page with three components calling `accessToken()` at once fires three
   * refreshes — and with rotation on, the second and third would present a
   * token the first had already retired, failing outside the 30-second grace
   * window and signing the user out.
   */
  const refreshInFlight = useRef<Promise<boolean> | null>(null);

  /**
   * Adopts a token response.
   *
   * `previousRefreshToken` is the fallback: rotation means a response normally
   * carries a replacement, but a response without one leaves the token we
   * already hold valid, and discarding it would sign the user out at the next
   * reload for no reason.
   */
  const adopt = useCallback((tokens: TokenSet, previousRefreshToken: string | null) => {
    tokensRef.current = tokens;

    const refreshToken = tokens.refreshToken ?? previousRefreshToken;
    if (refreshToken !== null) {
      writeRefreshToken(refreshToken);
    }

    const claims = readIdTokenClaims(tokens.idToken);
    setUser({ email: claims.email, subject: claims.subject });
    setStatus('signed-in');
    setRestoring(false);
    setError(null);
  }, []);

  /** Drops everything this app holds. The local half of sign-out. */
  const clearSession = useCallback(() => {
    tokensRef.current = null;
    clearRefreshToken();
    clearPendingAuthorization();
    setUser(null);
    setStatus('signed-out');
    setRestoring(false);
  }, []);

  const refresh = useCallback(async (): Promise<boolean> => {
    const existing = refreshInFlight.current;
    if (existing !== null) {
      return existing;
    }

    const attempt = (async (): Promise<boolean> => {
      const storedToken = readRefreshToken();
      if (config === null || storedToken === null) {
        clearSession();
        return false;
      }
      try {
        const tokens = await refreshTokens(config, storedToken);
        adopt(tokens, storedToken);
        return true;
      } catch {
        // Expired, revoked, or rotated away. Nothing recoverable, and the
        // caught value is not inspected — an OAuth failure body is the last
        // place to go looking for something safe to display.
        clearSession();
        return false;
      }
    })();

    refreshInFlight.current = attempt;
    try {
      return await attempt;
    } finally {
      refreshInFlight.current = null;
    }
  }, [adopt, clearSession, config]);

  /**
   * Bootstrap: a reload with a stored refresh token exchanges it in the
   * background. The status was already set to `signed-in` above; this effect
   * is what makes it true — `adopt` — or, on failure, what flips it to
   * `signed-out` through `clearSession`. Both end the `restoring` window.
   *
   * React 19 runs effects twice in development. The second run finds the first
   * run's exchange already in flight and shares it through `refreshInFlight`,
   * so the stored token is presented once, which rotation requires.
   */
  useEffect(() => {
    // No hosted UI, or nothing stored: already `signed-out`, no request.
    if (config === null || readRefreshToken() === null) {
      return;
    }
    void refresh();
    // Runs once per mount: `config` is fixed for the provider's lifetime and
    // `refresh` is stable, so this is a mount effect stated honestly.
  }, [config, refresh]);

  const accessToken = useCallback(async (): Promise<string | undefined> => {
    const current = tokensRef.current;
    if (current === null) {
      // Nothing in memory. With nothing stored either there is no session and
      // no request to make. With a stored refresh token this is the restore
      // window — the caller is a screen that rendered optimistically — so
      // wait for the exchange the bootstrap effect already started (the
      // in-flight promise is shared) rather than answering `undefined` and
      // sending the caller to sign-in over a session that is about to exist.
      if (readRefreshToken() === null) {
        return undefined;
      }
      const restored = await refresh();
      return restored ? (tokensRef.current?.accessToken ?? undefined) : undefined;
    }
    if (current.expiresAt - Date.now() > EXPIRY_SKEW_MS) {
      return current.accessToken;
    }
    // Proactive half of the refresh rule: at or near expiry, renew before
    // spending a round trip on a call the API would reject.
    const renewed = await refresh();
    return renewed ? (tokensRef.current?.accessToken ?? undefined) : undefined;
  }, [refresh]);

  const signIn = useCallback(
    async (returnTo?: string | null): Promise<void> => {
      if (config === null) {
        setError('Sign-in is not configured for this environment.');
        return;
      }
      const origin = currentOrigin();
      if (origin === null) {
        setError('Sign-in is only available in a browser.');
        return;
      }
      try {
        const { verifier, challenge } = await createPkcePair();
        const state = randomUrlSafeToken();

        // Written *before* the redirect: once the browser leaves, this module
        // is gone, and the verifier has to be waiting when it comes back.
        writePendingAuthorization({ state, codeVerifier: verifier, returnTo: returnTo ?? null });

        navigateTo(
          authorizeUrl(config, {
            redirectUri: callbackUrlFor(origin),
            state,
            codeChallenge: challenge,
          }),
        );
      } catch {
        // Only `CryptoUnavailableError` reaches here, and refusing is the
        // designed behaviour — see `pkce.ts`.
        setError('Sign-in could not be started securely in this browser.');
      }
    },
    [config],
  );

  const signOut = useCallback((): void => {
    clearSession();

    const origin = currentOrigin();
    if (config === null || origin === null) {
      return;
    }
    // The second leg. `logout_uri` is the bare origin — see `oauth.ts`.
    navigateTo(logoutUrl(config, origin));
  }, [clearSession, config]);

  const completeSignIn = useCallback(
    async (params: CallbackParams): Promise<CompleteSignInResult> => {
      // Read and discard in the same breath, before anything can fail. The
      // `state` and verifier are single-use by definition; leaving either in
      // storage after a failed attempt is what makes a callback replayable.
      const pending = readPendingAuthorization();
      clearPendingAuthorization();

      if (config === null) {
        return { ok: false, message: 'Sign-in is not configured for this environment.' };
      }
      if (params.error !== undefined && params.error !== '') {
        // The hosted UI declined (a cancelled sign-in sends `access_denied`).
        return { ok: false, message: 'Sign-in was not completed.' };
      }
      if (
        params.code === undefined ||
        params.code === '' ||
        params.state === undefined ||
        params.state === ''
      ) {
        return { ok: false, message: 'This sign-in link is incomplete. Start again.' };
      }
      // A missing or mismatched `state` is an error, never a silent retry: it
      // means this code was not requested by this browser (RFC 6749 §10.12).
      if (pending === null || pending.state !== params.state) {
        return { ok: false, message: 'This sign-in could not be verified. Start again.' };
      }

      const origin = currentOrigin();
      if (origin === null) {
        return { ok: false, message: 'Sign-in is only available in a browser.' };
      }

      try {
        const tokens = await exchangeCodeForTokens(config, {
          code: params.code,
          redirectUri: callbackUrlFor(origin),
          codeVerifier: pending.codeVerifier,
        });
        adopt(tokens, null);
        return { ok: true, returnTo: safeReturnTo(pending.returnTo) };
      } catch {
        clearSession();
        return { ok: false, message: 'Sign-in could not be completed. Start again.' };
      }
    },
    [adopt, clearSession, config],
  );

  const value = useMemo<SessionContextValue>(
    () => ({
      status,
      restoring,
      user,
      isConfigured: config !== null,
      error,
      signIn,
      signOut,
      accessToken,
      refresh,
      completeSignIn,
    }),
    [accessToken, completeSignIn, config, error, refresh, restoring, signIn, signOut, status, user],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

/**
 * The session. Throws outside a {@link SessionProvider} — see the header for
 * why that is better than a signed-out default.
 */
export function useSession(): SessionContextValue {
  const value = useContext(SessionContext);
  if (value === null) {
    throw new Error('useSession must be used inside a <SessionProvider>');
  }
  return value;
}

/**
 * Constrains a post-sign-in destination to a path **inside this app**.
 *
 * `returnTo` reaches storage from a query parameter, so it is attacker-shaped
 * input: without this, `/sign-in?returnTo=https://evil.example` would hand an
 * open redirect to anyone who could get that link clicked — and it would fire
 * immediately after a successful sign-in, which is the most credible moment
 * possible. A value must start with a single `/`; `//host` is rejected because
 * a browser reads it as protocol-relative and leaves the origin.
 *
 * **This function is the one place a runtime string becomes an `Href`, and the
 * cast below is why it has to be a function rather than an inline check.**
 * `typedRoutes` (see `app.config.ts`) makes `Href` a compile-time whitelist of
 * the routes that exist, which is exactly the right default and exactly what
 * cannot express "some path we will only learn about at runtime". Concentrating
 * the assertion here means there is a single guarded doorway rather than a cast
 * at each call site — and if the path turns out not to be a route, the
 * `+not-found` screen answers, which is the correct outcome for a stale deep
 * link anyway.
 */
export function safeReturnTo(candidate: string | null | undefined): Href {
  if (typeof candidate !== 'string') {
    return '/';
  }
  if (!candidate.startsWith('/') || candidate.startsWith('//')) {
    return '/';
  }
  return candidate as Href;
}
