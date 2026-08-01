/**
 * The three hosted-UI endpoints this app talks to, and nothing else.
 *
 * All of it is plain OAuth 2.0 against the Cognito **hosted domain** — no AWS
 * SDK, no Amplify. That is a decision, not an omission
 * (`docs/adr/0007-hosted-ui-pkce-refresh-token-in-local-storage.md`), and the
 * refresh path in particular has no alternative: the web app client sets
 * `explicit_auth_flows = ["ALLOW_USER_SRP_AUTH"]` and deliberately omits the
 * SDK's refresh-token auth flow, because Cognito **rejects** that flow outright
 * when refresh-token rotation is enabled. Rotation is enabled, so
 * `POST /oauth2/token` with `grant_type=refresh_token` is the only way to
 * refresh, and `infra/modules/auth/main.tf` carries the same note beside the
 * resource.
 *
 * The client is **public**: `generate_secret = false`, so no request here sends
 * a client secret or an `Authorization` header. A browser bundle can be
 * unpacked, which is why there is no secret to send.
 *
 * **No token is ever put in a message, a log, or an error.** Errors carry the
 * OAuth `error` code and the HTTP status, both of which are safe, and nothing
 * from the response body beyond that.
 */

import type { AuthConfig } from '@/config/environment';
import { CODE_CHALLENGE_METHOD } from '@/session/pkce';

/**
 * The scopes the app client allows (`allowed_oauth_scopes` in
 * `infra/modules/auth/main.tf`). `email` and `profile` are what put an `email`
 * claim in the ID token, which is the only place the app can read the user's
 * address from — the access token's `username` is a Cognito UUID.
 */
export const OAUTH_SCOPES = 'openid email profile';

/** The path the hosted UI redirects back to. Pinned by infra — see the route file. */
export const CALLBACK_PATH = '/auth/callback';

/** A set of tokens as the session holds them. */
export interface TokenSet {
  /** The bearer credential for the API. Memory only, never persisted. */
  readonly accessToken: string;
  /** Read for display identity (the `email` claim). Memory only. */
  readonly idToken: string | null;
  /**
   * The one persisted credential. `null` on a refresh response that did not
   * rotate — the caller keeps the token it already had.
   */
  readonly refreshToken: string | null;
  /** Absolute expiry of {@link accessToken}, epoch milliseconds. */
  readonly expiresAt: number;
}

/**
 * A token-endpoint failure.
 *
 * `code` is the OAuth `error` value (`invalid_grant` when a refresh token has
 * been rotated away, revoked, or expired) — the one field worth branching on,
 * and safe to surface.
 */
export class OAuthError extends Error {
  readonly code: string;
  readonly statusCode: number;

  constructor(code: string, statusCode: number, message?: string) {
    super(message ?? `the authorization server rejected the request (${code})`);
    this.name = 'OAuthError';
    this.code = code;
    this.statusCode = statusCode;
  }
}

/**
 * `application/x-www-form-urlencoded`, used for both the query strings and the
 * request bodies.
 *
 * Hand-rolled rather than `URLSearchParams` because React Native's `URL`
 * family is a partial polyfill with a history of surprises, and this is six
 * lines. `encodeURIComponent` is what RFC 6749 §B asks for.
 */
function formEncode(params: Record<string, string>): string {
  return Object.entries(params)
    .map(([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(value)}`)
    .join('&');
}

/** The redirect URI for an origin. Cognito matches this **exactly**. */
export function callbackUrlFor(origin: string): string {
  return `${origin}${CALLBACK_PATH}`;
}

/** Parameters for {@link authorizeUrl}. */
export interface AuthorizeUrlParams {
  readonly redirectUri: string;
  readonly state: string;
  readonly codeChallenge: string;
}

/**
 * Builds the `/oauth2/authorize` URL that starts sign-in.
 *
 * `code_challenge` and `code_challenge_method=S256` are the PKCE half, and the
 * reason `oauth.test.ts` asserts on this string: ADR 0007 makes "we send PKCE"
 * an assertion about *our code*, because the pool cannot enforce it.
 */
export function authorizeUrl(config: AuthConfig, params: AuthorizeUrlParams): string {
  const query = formEncode({
    response_type: 'code',
    client_id: config.clientId,
    redirect_uri: params.redirectUri,
    scope: OAUTH_SCOPES,
    state: params.state,
    code_challenge: params.codeChallenge,
    code_challenge_method: CODE_CHALLENGE_METHOD,
  });
  return `${config.domain}/oauth2/authorize?${query}`;
}

/**
 * Builds the `/logout` URL that ends the hosted-UI session.
 *
 * The parameter is **`logout_uri`**, and its value is the bare origin — that is
 * what `web_logout_urls` registers in `infra/modules/auth/main.tf`
 * (`web_logout_urls = var.web_origins`, no path), and Cognito matches it
 * exactly. Sending `redirect_uri`, or an origin with a path on the end, gets a
 * generic Cognito error page instead of a sign-out.
 */
export function logoutUrl(config: AuthConfig, logoutUri: string): string {
  const query = formEncode({ client_id: config.clientId, logout_uri: logoutUri });
  return `${config.domain}/logout?${query}`;
}

/** The transport, injectable so tests never touch the network. */
export type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

function platformFetch(): FetchLike {
  // Wrapped rather than passed by reference: browsers throw "Illegal
  // invocation" on a `fetch` detached from its receiver.
  return (input, init) => globalThis.fetch(input, init);
}

/** Parameters for {@link exchangeCodeForTokens}. */
export interface CodeExchangeParams {
  readonly code: string;
  readonly redirectUri: string;
  readonly codeVerifier: string;
}

/**
 * Exchanges an authorization code for tokens (RFC 6749 §4.1.3 + RFC 7636 §4.5).
 *
 * `redirect_uri` is required even though the redirect already happened — the
 * server compares it against the one from the authorize call — and
 * `code_verifier` is what proves this is the same client that started the flow.
 */
export async function exchangeCodeForTokens(
  config: AuthConfig,
  params: CodeExchangeParams,
  fetchImpl: FetchLike = platformFetch(),
): Promise<TokenSet> {
  return postToken(
    config,
    {
      grant_type: 'authorization_code',
      client_id: config.clientId,
      code: params.code,
      redirect_uri: params.redirectUri,
      code_verifier: params.codeVerifier,
    },
    fetchImpl,
  );
}

/**
 * Trades a refresh token for a fresh access token (RFC 6749 §6).
 *
 * **Rotation is enabled on the app client**, so a success here normally returns
 * a *new* refresh token that replaces the stored one and retires the one just
 * used. The caller must persist the replacement; not doing so signs the user
 * out at the next reload. Cognito allows a 30-second grace period in which the
 * retired token still works, which is what keeps a lost response from locking
 * the client out.
 */
export async function refreshTokens(
  config: AuthConfig,
  refreshToken: string,
  fetchImpl: FetchLike = platformFetch(),
): Promise<TokenSet> {
  return postToken(
    config,
    {
      grant_type: 'refresh_token',
      client_id: config.clientId,
      refresh_token: refreshToken,
    },
    fetchImpl,
  );
}

/**
 * The one place a request reaches `/oauth2/token`.
 *
 * No `Authorization` header and no `client_secret` in the body: this is a
 * public client (RFC 6749 §2.1), and Cognito rejects a secret it never issued.
 */
async function postToken(
  config: AuthConfig,
  body: Record<string, string>,
  fetchImpl: FetchLike,
): Promise<TokenSet> {
  const response = await fetchImpl(`${config.domain}/oauth2/token`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/x-www-form-urlencoded',
      Accept: 'application/json',
    },
    body: formEncode(body),
  });

  const raw = await response.text();
  const payload = parseJsonObject(raw);

  if (!response.ok) {
    throw new OAuthError(errorCodeOf(payload), response.status);
  }
  if (payload === null) {
    throw new OAuthError('invalid_response', response.status);
  }
  return toTokenSet(payload, response.status);
}

/**
 * Reads the OAuth `error` code from a failure body.
 *
 * Only the `error` field is read. `error_description` is deliberately ignored:
 * it is free text from the server that ends up in UI and logs, and this is the
 * one code path where a mistake could put credential material there.
 */
function errorCodeOf(payload: Record<string, unknown> | null): string {
  const code = payload?.error;
  return typeof code === 'string' && code !== '' ? code : 'invalid_request';
}

function parseJsonObject(raw: string): Record<string, unknown> | null {
  try {
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      return null;
    }
    return parsed as Record<string, unknown>;
  } catch {
    return null;
  }
}

/**
 * Maps a token response onto {@link TokenSet}.
 *
 * `expires_in` is seconds-from-now; it becomes an absolute epoch-millisecond
 * deadline here so nothing downstream has to remember when the response
 * arrived. A missing or unusable `expires_in` falls back to a **conservative**
 * 60 seconds rather than the pool's real hour: erring short costs one extra
 * refresh, erring long sends expired tokens to the API.
 */
function toTokenSet(payload: Record<string, unknown>, statusCode: number): TokenSet {
  const accessToken = payload.access_token;
  if (typeof accessToken !== 'string' || accessToken === '') {
    throw new OAuthError('invalid_response', statusCode);
  }
  const idToken = payload.id_token;
  const refreshToken = payload.refresh_token;
  const expiresIn = payload.expires_in;

  const lifetimeSeconds = typeof expiresIn === 'number' && expiresIn > 0 ? expiresIn : 60;

  return {
    accessToken,
    idToken: typeof idToken === 'string' && idToken !== '' ? idToken : null,
    refreshToken: typeof refreshToken === 'string' && refreshToken !== '' ? refreshToken : null,
    expiresAt: Date.now() + lifetimeSeconds * 1000,
  };
}
