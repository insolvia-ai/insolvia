/**
 * The staff session: memory-only tokens, sessionStorage only for the
 * redirect handshake.
 *
 * TOKENS NEVER TOUCH STORAGE. The ID token lives in a ref and dies with the
 * tab (#209, ADR 0011) — the deliberate divergence from the app's ADR 0007
 * localStorage refresh token. What DOES use sessionStorage is the PKCE
 * handshake (`state`, `code_verifier`, `returnTo`): those must survive the
 * full-page redirect to Google and back, are single-use, are cleared on
 * completion, and none of them is a credential — the verifier is worthless
 * without the authorization code Google only hands to the registered
 * redirect URI.
 *
 * Expiry is handled by re-authentication, not refresh: `token()` answers
 * null once the ID token is inside its final minute, callers treat null as
 * signed-out, and Google's own session makes the round trip near-invisible
 * for a signed-in Workspace account.
 */

import {
  createContext,
  useCallback,
  useContext,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";

import { config } from "../config/environment";
import {
  authorizeUrl,
  callbackUrlFor,
  exchangeCodeForTokens,
  type StaffTokens,
} from "./google-oauth";
import { base64UrlDecode, createPkcePair, randomUrlSafeToken } from "./pkce";

const STATE_KEY = "insolvia-admin.oauth.state";
const VERIFIER_KEY = "insolvia-admin.oauth.verifier";
const RETURN_TO_KEY = "insolvia-admin.oauth.return-to";

/** Refuse to present a token this close to expiry — a request in flight when
 * it lapses would 401 anyway; better to re-authenticate up front. */
const EXPIRY_MARGIN_MS = 60_000;

export interface StaffSession {
  /** Signed-in address for display, from the ID token's email claim. */
  readonly email: string | null;
  /** True when a usable token is held. */
  readonly signedIn: boolean;
  /** The current ID token, or null when absent/expiring — callers treat null as signed-out. */
  token(): string | null;
  /** Starts the Google redirect. `returnTo` must be an in-app path. */
  signIn(returnTo?: string): Promise<void>;
  /** Completes the redirect; returns the in-app path to land on. */
  completeSignIn(params: URLSearchParams): Promise<string>;
  /** Drops the session. Google's own session is untouched — signing the
   * Workspace account out of Google is Google's UI's job, not ours. */
  signOut(): void;
}

const SessionContext = createContext<StaffSession | null>(null);

/** Only ever an in-app absolute path — the same open-redirect guard the
 * app's session provider applies. */
function safeReturnTo(value: string | null): string {
  if (value === null || !value.startsWith("/") || value.startsWith("//")) {
    return "/";
  }
  return value;
}

function emailFromIdToken(idToken: string): string | null {
  const payload = idToken.split(".")[1];
  if (payload === undefined) return null;
  const bytes = base64UrlDecode(payload);
  if (bytes === null) return null;
  try {
    const claims = JSON.parse(new TextDecoder().decode(bytes)) as {
      email?: unknown;
    };
    return typeof claims.email === "string" ? claims.email : null;
  } catch {
    return null;
  }
}

export function SessionProvider({ children }: { children: ReactNode }) {
  // Ref, not state: a token is read at request time, never rendered, and a
  // re-render per token event would be a re-render for nothing.
  const tokens = useRef<StaffTokens | null>(null);
  const [email, setEmail] = useState<string | null>(null);
  const [signedIn, setSignedIn] = useState(false);

  const token = useCallback((): string | null => {
    const held = tokens.current;
    if (held === null) return null;
    if (Date.now() >= held.expiresAt - EXPIRY_MARGIN_MS) {
      tokens.current = null;
      return null;
    }
    return held.idToken;
  }, []);

  const signIn = useCallback(async (returnTo?: string) => {
    const state = randomUrlSafeToken();
    const pkce = await createPkcePair();
    sessionStorage.setItem(STATE_KEY, state);
    sessionStorage.setItem(VERIFIER_KEY, pkce.verifier);
    sessionStorage.setItem(RETURN_TO_KEY, safeReturnTo(returnTo ?? null));
    window.location.assign(
      authorizeUrl(config, {
        redirectUri: callbackUrlFor(window.location.origin),
        state,
        codeChallenge: pkce.challenge,
      }),
    );
  }, []);

  const completeSignIn = useCallback(
    async (params: URLSearchParams): Promise<string> => {
      const expectedState = sessionStorage.getItem(STATE_KEY);
      const verifier = sessionStorage.getItem(VERIFIER_KEY);
      const returnTo = safeReturnTo(sessionStorage.getItem(RETURN_TO_KEY));
      // Single-use whatever happens next — a handshake that failed must not
      // be replayable against a second callback.
      sessionStorage.removeItem(STATE_KEY);
      sessionStorage.removeItem(VERIFIER_KEY);
      sessionStorage.removeItem(RETURN_TO_KEY);

      const code = params.get("code");
      const state = params.get("state");
      if (
        code === null ||
        state === null ||
        expectedState === null ||
        verifier === null ||
        state !== expectedState
      ) {
        throw new Error("the sign-in response did not match this session");
      }

      const next = await exchangeCodeForTokens(config, {
        code,
        codeVerifier: verifier,
        redirectUri: callbackUrlFor(window.location.origin),
      });
      tokens.current = next;
      setEmail(emailFromIdToken(next.idToken));
      setSignedIn(true);
      return returnTo;
    },
    [],
  );

  const signOut = useCallback(() => {
    tokens.current = null;
    setEmail(null);
    setSignedIn(false);
  }, []);

  const session = useMemo<StaffSession>(
    () => ({ email, signedIn, token, signIn, completeSignIn, signOut }),
    [email, signedIn, token, signIn, completeSignIn, signOut],
  );

  return (
    <SessionContext.Provider value={session}>{children}</SessionContext.Provider>
  );
}

export function useSession(): StaffSession {
  const session = useContext(SessionContext);
  if (session === null) {
    throw new Error("useSession requires a SessionProvider ancestor");
  }
  return session;
}
