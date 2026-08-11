/**
 * The staff session: memory-only tokens, and no storage at all.
 *
 * TOKENS NEVER TOUCH STORAGE. The ID token lives in a ref and dies with the
 * tab (#209, ADR 0011) — the deliberate divergence from the app's ADR 0007
 * localStorage refresh token. Since the move to Google Identity Services
 * (see session/google-identity.ts for why the PKCE redirect could never
 * work), not even a handshake touches sessionStorage: the button hands
 * `acceptCredential` an ID token in the same page load that renders it.
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
} from 'react';

/** The session as this portal holds it: one credential, one expiry. */
export interface StaffTokens {
  /** The Google ID token — the bearer credential for the admin service. */
  readonly idToken: string;
  /** Absolute expiry, epoch milliseconds. */
  readonly expiresAt: number;
}

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
  /**
   * Takes the ID token Google's sign-in button produced. Throws when the
   * credential cannot carry a session (no parsable expiry) — the sign-in
   * screen reports that; nothing is stored.
   */
  acceptCredential(credential: string): void;
  /** Drops the session. Google's own session is untouched — signing the
   * Workspace account out of Google is Google's UI's job, not ours. */
  signOut(): void;
}

const SessionContext = createContext<StaffSession | null>(null);

function base64UrlDecode(value: string): Uint8Array | null {
  const padded = value.replace(/-/g, '+').replace(/_/g, '/');
  try {
    const raw = atob(padded);
    const bytes = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i += 1) {
      bytes[i] = raw.charCodeAt(i);
    }
    return bytes;
  } catch {
    return null;
  }
}

/**
 * The two claims the PORTAL needs from the credential — display address and
 * expiry. This is not verification and must never grow into it: the admin
 * service verifies signature, issuer, audience, `hd` and `email_verified` on
 * every request, and a forged value here buys its holder a UI shell whose
 * every call 401s.
 */
function sessionClaims(credential: string): {
  email: string | null;
  expiresAt: number;
} {
  const payload = credential.split('.')[1];
  const bytes = payload === undefined ? null : base64UrlDecode(payload);
  if (bytes === null) {
    throw new Error('the credential is not a JWT');
  }
  let claims: { email?: unknown; exp?: unknown };
  try {
    claims = JSON.parse(new TextDecoder().decode(bytes)) as {
      email?: unknown;
      exp?: unknown;
    };
  } catch {
    throw new Error('the credential payload is not JSON');
  }
  if (typeof claims.exp !== 'number') {
    throw new Error('the credential carries no expiry');
  }
  return {
    email: typeof claims.email === 'string' ? claims.email : null,
    expiresAt: claims.exp * 1000,
  };
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

  const acceptCredential = useCallback((credential: string) => {
    const claims = sessionClaims(credential);
    tokens.current = { idToken: credential, expiresAt: claims.expiresAt };
    setEmail(claims.email);
    setSignedIn(true);
  }, []);

  const signOut = useCallback(() => {
    tokens.current = null;
    setEmail(null);
    setSignedIn(false);
  }, []);

  const session = useMemo<StaffSession>(
    () => ({ email, signedIn, token, acceptCredential, signOut }),
    [email, signedIn, token, acceptCredential, signOut],
  );

  return <SessionContext.Provider value={session}>{children}</SessionContext.Provider>;
}

export function useSession(): StaffSession {
  const session = useContext(SessionContext);
  if (session === null) {
    throw new Error('useSession requires a SessionProvider ancestor');
  }
  return session;
}
