/**
 * What the session writes down, and where.
 *
 * The split is the whole of ADR 0007's storage decision, so it is worth reading
 * as one picture:
 *
 * | Value | Lives in | Survives |
 * |---|---|---|
 * | access token | a React ref in the provider | nothing — memory only |
 * | ID token | a React ref in the provider | nothing — memory only |
 * | **refresh token** | `localStorage` | a reload, a new tab, a browser restart |
 * | `state`, `code_verifier`, `returnTo` | `sessionStorage` | the redirect to the hosted UI and back, this tab only |
 *
 * Access and ID tokens are **absent from this file entirely** — there is no
 * function here that could write one, which is the point. The refresh token is
 * the single persisted credential, and the accepted cost is that a successful
 * XSS can read it (ADR 0007, *Consequences*).
 *
 * The three transient values are `sessionStorage`, not `localStorage`, because
 * they are per-attempt: two tabs starting sign-in at once must not overwrite
 * each other's verifier, and an abandoned attempt must not outlive the tab.
 */

import { persistentStore, readFrom, removeFrom, transientStore, writeTo } from '@/platform/browser';

/**
 * Storage keys, namespaced so the app cannot collide with anything else on the
 * origin. Changing one signs every existing session out at the next reload.
 */
const REFRESH_TOKEN_KEY = 'insolvia.auth.refresh-token';
const PENDING_AUTHORIZATION_KEY = 'insolvia.auth.pending-authorization';

/** The persisted refresh token, or `null` when there is no stored session. */
export function readRefreshToken(): string | null {
  const value = readFrom(persistentStore(), REFRESH_TOKEN_KEY);
  return value === null || value === '' ? null : value;
}

/**
 * Persists the refresh token, replacing any previous one.
 *
 * Called on every successful token response, because rotation means each one
 * carries a *different* token and the old one is retired.
 */
export function writeRefreshToken(token: string): void {
  writeTo(persistentStore(), REFRESH_TOKEN_KEY, token);
}

/** Removes the persisted refresh token. Half of what sign-out means. */
export function clearRefreshToken(): void {
  removeFrom(persistentStore(), REFRESH_TOKEN_KEY);
}

/**
 * The state carried across the redirect to the hosted UI.
 *
 * `state` is the CSRF defence (RFC 6749 §10.12): the value that comes back must
 * be the value that went out, or the code belongs to somebody else's flow.
 * `codeVerifier` is the PKCE secret that proves this client started it.
 * `returnTo` is product, not security — where the user was headed before the
 * guard sent them to sign in.
 */
export interface PendingAuthorization {
  readonly state: string;
  readonly codeVerifier: string;
  readonly returnTo: string | null;
}

/** Stores the pending attempt. One at a time, per tab; a new attempt replaces it. */
export function writePendingAuthorization(pending: PendingAuthorization): void {
  writeTo(transientStore(), PENDING_AUTHORIZATION_KEY, JSON.stringify(pending));
}

/**
 * Reads the pending attempt, or `null` if there is none or it is unreadable.
 *
 * Every field is checked rather than cast: this value round-trips through
 * storage the user can edit, and a `codeVerifier` that is silently `undefined`
 * would produce an opaque `invalid_grant` at the token endpoint. A malformed
 * record reads as absent, which the caller already treats as a failed
 * `state` check.
 */
export function readPendingAuthorization(): PendingAuthorization | null {
  const raw = readFrom(transientStore(), PENDING_AUTHORIZATION_KEY);
  if (raw === null || raw === '') {
    return null;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }
  if (typeof parsed !== 'object' || parsed === null) {
    return null;
  }
  const candidate = parsed as Record<string, unknown>;
  const { state, codeVerifier, returnTo } = candidate;
  if (typeof state !== 'string' || state === '') {
    return null;
  }
  if (typeof codeVerifier !== 'string' || codeVerifier === '') {
    return null;
  }
  return {
    state,
    codeVerifier,
    returnTo: typeof returnTo === 'string' && returnTo !== '' ? returnTo : null,
  };
}

/**
 * Discards the pending attempt.
 *
 * The callback screen calls this **immediately after reading**, before the code
 * exchange is attempted and whatever its outcome. A `state` left in storage is
 * a replayable value, and a `code_verifier` left behind would let a second,
 * unrelated callback appear to validate.
 */
export function clearPendingAuthorization(): void {
  removeFrom(transientStore(), PENDING_AUTHORIZATION_KEY);
}
