import type { Href } from 'expo-router';
import { createContext, useCallback, useContext, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';

import { resolvePortalAuthConfig } from '@/config/environment';
import type { AuthConfig } from '@/config/environment';
import { currentOrigin, navigateTo } from '@/platform/browser';
import { readIdTokenClaims } from '@/session/id-token';
import {
  PORTAL_CALLBACK_PATH,
  PORTAL_HOME_PATH,
  authorizeUrl,
  callbackUrlFor,
  exchangeCodeForTokens,
  logoutUrl,
  refreshTokens,
} from '@/session/oauth';
import type { TokenSet } from '@/session/oauth';
import { createPkcePair, randomUrlSafeToken } from '@/session/pkce';
import type {
  CallbackParams,
  CompleteSignInResult,
  SessionStatus,
  SessionUser,
} from '@/session/session-provider';
import {
  clearPortalPendingAuthorization,
  readPortalPendingAuthorization,
  writePortalPendingAuthorization,
} from '@/session/token-store';

/**
 * The CLIENT PORTAL's session (ADR 0023) — a second `SessionProvider`, not a
 * parameter on the first.
 *
 * The two differ in exactly the property ADR 0007 spent an ADR on:
 * persistence. The staff session keeps its refresh token in `localStorage`, a
 * costed trade for a 30-day "stay signed in". This one keeps **every token in
 * memory, the refresh token included** — ADR 0011's choice, not ADR 0007's —
 * because a debtor's device is the least controlled in the system (a shared
 * family computer, a library terminal), and what the portal holds resumes
 * server-side, so a lost session costs one sign-in and nothing else. A flag
 * on the staff provider would have been one boolean away from writing a
 * debtor's refresh token to disk; a separate provider has no code that could.
 *
 * What it shares with the staff session is everything that is not storage:
 * the hosted UI's three endpoints (`oauth.ts`), PKCE (`pkce.ts`), the display
 * identity (`id-token.ts`) and the callback's rules — `state` checked,
 * verifier single-use, `returnTo` constrained. The one thing it writes down is
 * the PENDING attempt (`state`, verifier, `returnTo`) in `sessionStorage`,
 * under its own key: it has to survive the redirect to the hosted UI and back,
 * it is not a credential, and it is cleared the moment the callback reads it.
 *
 * ## The state machine
 *
 * ```text
 *   every page load           ┌─────────────┐  signIn()   managed login
 *   ────────────────────────► │ signed-out  │ ──────────► /oauth2/authorize
 *                             └─────────────┘                  │
 *                                   ▲                          ▼
 *                refresh fails /    │                 /portal/auth/callback
 *                signOut()          │                          │
 *                             ┌─────┴───────┐  completeSignIn()│
 *                             │  signed-in  │ ◄────────────────┘
 *                             └─────────────┘
 * ```
 *
 * **Every page load starts signed out**, because nothing that could restore a
 * session survives one. There is therefore no `restoring` state to wait on —
 * the staff provider's optimistic start does not apply. A reload sends the
 * client back through sign-in, where Cognito's own session cookie on the
 * managed login pages usually signs them straight back in without a password:
 * that cookie is Cognito's to keep, on Cognito's domain, and is cleared by the
 * `/logout` leg of {@link PortalSessionContextValue.signOut}.
 *
 * Portal tokens are the pool's portal client's: one-hour access, one-day
 * refresh with rotation (`infra/modules/auth/main.tf`). The in-memory refresh
 * token keeps a long session in one tab alive across the hour.
 */
export interface PortalSessionContextValue {
  readonly status: SessionStatus;
  /** Display identity from the ID token. Never used for authorization. */
  readonly user: SessionUser | null;
  /** Whether this build has a portal app client to sign in against. */
  readonly isConfigured: boolean;
  /** A message for a sign-in that could not be *started*, else `null`. */
  readonly error: string | null;
  /** Leaves for the managed login pages; `returnTo` must be a portal path. */
  signIn(returnTo?: string | null): Promise<void>;
  /**
   * Both legs: forgets the tokens here, then sends the browser to the hosted
   * UI's `/logout` so Cognito's cookie goes too — on a shared computer the
   * next person must not be signed straight back in as this client.
   */
  signOut(): void;
  /** The access token for a portal API call, refreshed first near expiry. */
  accessToken(): Promise<string | undefined>;
  /** Forces one refresh (the reactive half of the 401 rule). */
  refresh(): Promise<boolean>;
  /** Completes the code exchange. Called only by the portal callback screen. */
  completeSignIn(params: CallbackParams): Promise<CompleteSignInResult>;
}

/** Refresh this far before expiry — the staff provider's value and reason. */
const EXPIRY_SKEW_MS = 60_000;

const PortalSessionContext = createContext<PortalSessionContextValue | null>(null);

export interface PortalSessionProviderProps {
  children: ReactNode;
  /** Test seam, as on the staff provider. Production never passes it. */
  config?: AuthConfig | null;
}

/** Mounted once, in `src/app/portal/_layout.tsx`, around the portal's routes. */
export function PortalSessionProvider({
  children,
  config: configOverride,
}: PortalSessionProviderProps) {
  const [config] = useState<AuthConfig | null>(() =>
    configOverride !== undefined ? configOverride : resolvePortalAuthConfig(),
  );
  const [status, setStatus] = useState<SessionStatus>('signed-out');
  const [user, setUser] = useState<SessionUser | null>(null);
  const [error, setError] = useState<string | null>(null);

  /**
   * EVERY token — access, ID and refresh. Memory only, never state (the
   * staff provider's reason: a bearer credential in React state lands in the
   * fiber tree and every DevTools session). Nothing in this file writes any
   * of them anywhere else; `portal-session-provider.test.tsx` asserts that
   * `localStorage` is untouched by a whole sign-in and refresh.
   */
  const tokensRef = useRef<TokenSet | null>(null);
  const refreshInFlight = useRef<Promise<boolean> | null>(null);

  const adopt = useCallback((tokens: TokenSet, previousRefreshToken: string | null) => {
    tokensRef.current = {
      ...tokens,
      refreshToken: tokens.refreshToken ?? previousRefreshToken,
    };
    const claims = readIdTokenClaims(tokens.idToken);
    setUser({ email: claims.email, subject: claims.subject });
    setStatus('signed-in');
    setError(null);
  }, []);

  const clearSession = useCallback(() => {
    tokensRef.current = null;
    clearPortalPendingAuthorization();
    setUser(null);
    setStatus('signed-out');
  }, []);

  const refresh = useCallback(async (): Promise<boolean> => {
    const existing = refreshInFlight.current;
    if (existing !== null) {
      return existing;
    }
    const attempt = (async (): Promise<boolean> => {
      const held = tokensRef.current?.refreshToken ?? null;
      if (config === null || held === null) {
        clearSession();
        return false;
      }
      try {
        adopt(await refreshTokens(config, held), held);
        return true;
      } catch {
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

  const accessToken = useCallback(async (): Promise<string | undefined> => {
    const current = tokensRef.current;
    if (current === null) {
      return undefined;
    }
    if (current.expiresAt - Date.now() > EXPIRY_SKEW_MS) {
      return current.accessToken;
    }
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
        writePortalPendingAuthorization({
          state,
          codeVerifier: verifier,
          returnTo: returnTo ?? null,
        });
        navigateTo(
          authorizeUrl(config, {
            redirectUri: callbackUrlFor(origin, PORTAL_CALLBACK_PATH),
            state,
            codeChallenge: challenge,
          }),
        );
      } catch {
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
    // The portal client's registered logout URI carries a path — see
    // `PORTAL_HOME_PATH`. The bare origin would be refused by Cognito.
    navigateTo(logoutUrl(config, `${origin}${PORTAL_HOME_PATH}`));
  }, [clearSession, config]);

  const completeSignIn = useCallback(
    async (params: CallbackParams): Promise<CompleteSignInResult> => {
      // Read and discard before anything can fail — single-use by definition.
      const pending = readPortalPendingAuthorization();
      clearPortalPendingAuthorization();

      if (config === null) {
        return { ok: false, message: 'Sign-in is not configured for this environment.' };
      }
      if (params.error !== undefined && params.error !== '') {
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
          redirectUri: callbackUrlFor(origin, PORTAL_CALLBACK_PATH),
          codeVerifier: pending.codeVerifier,
        });
        adopt(tokens, null);
        return { ok: true, returnTo: safePortalReturnTo(pending.returnTo) };
      } catch {
        clearSession();
        return { ok: false, message: 'Sign-in could not be completed. Start again.' };
      }
    },
    [adopt, clearSession, config],
  );

  const value = useMemo<PortalSessionContextValue>(
    () => ({
      status,
      user,
      isConfigured: config !== null,
      error,
      signIn,
      signOut,
      accessToken,
      refresh,
      completeSignIn,
    }),
    [accessToken, completeSignIn, config, error, refresh, signIn, signOut, status, user],
  );

  return <PortalSessionContext.Provider value={value}>{children}</PortalSessionContext.Provider>;
}

/** The portal session. Throws outside a {@link PortalSessionProvider}. */
export function usePortalSession(): PortalSessionContextValue {
  const value = useContext(PortalSessionContext);
  if (value === null) {
    throw new Error('usePortalSession must be used inside a <PortalSessionProvider>');
  }
  return value;
}

/**
 * Constrains a post-sign-in destination to a path **inside the portal**.
 *
 * Stricter than the staff `safeReturnTo`: besides refusing anything that
 * leaves the origin, it refuses any in-app path OUTSIDE `/portal`. A portal
 * sign-in that returned a client to a staff route would land a debtor on a
 * screen whose every call answers 401 — harmless, since the API refuses a
 * portal token there, but a confusing dead end, and a link a firm could be
 * phished into sending. The callback itself is refused too: returning to it
 * would replay an already-consumed code.
 */
export function safePortalReturnTo(candidate: string | null | undefined): Href {
  if (typeof candidate !== 'string' || candidate.startsWith('//')) {
    return PORTAL_HOME_PATH;
  }
  const inPortal = candidate === PORTAL_HOME_PATH || candidate.startsWith(`${PORTAL_HOME_PATH}/`);
  if (!inPortal || candidate.startsWith(PORTAL_CALLBACK_PATH)) {
    return PORTAL_HOME_PATH;
  }
  return candidate as Href;
}
