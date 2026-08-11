/**
 * The two Google endpoints this portal talks to, and nothing else.
 *
 * Plain OAuth 2.0 authorization-code + PKCE against Google's public
 * endpoints — the same shape as the app's Cognito module (which this adapts),
 * with the differences #209 decided:
 *
 *   - The credential the API consumes is the ID TOKEN, not the access token:
 *     Google's access tokens are opaque, and the admin service verifies ID
 *     tokens (issuer/audience/hd) — ADR 0011 records the deviation.
 *   - NO REFRESH TOKEN, anywhere. Tokens live in memory and die with the
 *     tab; when the ID token expires, the portal sends the user back through
 *     Google, which for an already-signed-in Workspace account is a
 *     redirect round-trip measured in seconds. ADR 0007's localStorage
 *     trade bought attorneys 30 signed-in days; a high-privilege internal
 *     tool does not want that arm of the XSS trade at all.
 *
 * The client is public (no secret exists — it was created without one), and
 * no token is ever put in a message, a log, or an error.
 */

import { CODE_CHALLENGE_METHOD } from "./pkce";
import type { AdminConfig } from "../config/environment";

export const AUTHORIZE_ENDPOINT = "https://accounts.google.com/o/oauth2/v2/auth";
export const TOKEN_ENDPOINT = "https://oauth2.googleapis.com/token";

/**
 * `openid email` and nothing more: the service checks `email_verified` and
 * `hd` from the ID token, and the portal displays the address. No profile,
 * no offline_access — offline access is a refresh token, which this client
 * must never hold.
 */
export const OAUTH_SCOPES = "openid email";

/** The path Google redirects back to. Registered per environment, exact-match. */
export const CALLBACK_PATH = "/auth/callback";

/** The session as this portal holds it: one credential, one expiry. */
export interface StaffTokens {
  /** The Google ID token — the bearer credential for the admin service. */
  readonly idToken: string;
  /** Absolute expiry, epoch milliseconds. */
  readonly expiresAt: number;
}

/** A token-endpoint failure; `code` is the OAuth `error` value. */
export class OAuthError extends Error {
  readonly code: string;
  readonly statusCode: number;

  constructor(code: string, statusCode: number, message?: string) {
    super(message ?? `the authorization server rejected the request (${code})`);
    this.name = "OAuthError";
    this.code = code;
    this.statusCode = statusCode;
  }
}

function formEncode(params: Record<string, string>): string {
  return Object.entries(params)
    .map(
      ([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(value)}`,
    )
    .join("&");
}

/** The redirect URI for an origin. Google matches this exactly. */
export function callbackUrlFor(origin: string): string {
  return `${origin}${CALLBACK_PATH}`;
}

export interface AuthorizeUrlParams {
  readonly redirectUri: string;
  readonly state: string;
  readonly codeChallenge: string;
}

/**
 * Builds the Google authorize URL that starts sign-in.
 *
 * `hd` is a HINT to Google's account chooser (pre-filters to Workspace
 * accounts); the ENFORCEMENT is the client being Internal on Google's side
 * and the service's `hd` claim check on ours — the test asserts the hint is
 * sent, but nothing relies on it.
 */
export function authorizeUrl(
  config: AdminConfig,
  params: AuthorizeUrlParams,
): string {
  const query = formEncode({
    response_type: "code",
    client_id: config.googleClientId,
    redirect_uri: params.redirectUri,
    scope: OAUTH_SCOPES,
    state: params.state,
    code_challenge: params.codeChallenge,
    code_challenge_method: CODE_CHALLENGE_METHOD,
    hd: "insolvia.ai",
  });
  return `${AUTHORIZE_ENDPOINT}?${query}`;
}

interface TokenResponse {
  id_token?: unknown;
  expires_in?: unknown;
  error?: unknown;
}

/**
 * Exchanges the authorization code (plus the PKCE verifier) for tokens.
 *
 * Google also returns an access token; it is deliberately dropped — nothing
 * in this portal calls a Google API, and a credential nothing uses is a
 * credential nothing should hold.
 */
export async function exchangeCodeForTokens(
  config: AdminConfig,
  params: {
    readonly code: string;
    readonly codeVerifier: string;
    readonly redirectUri: string;
  },
): Promise<StaffTokens> {
  const response = await fetch(TOKEN_ENDPOINT, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: formEncode({
      grant_type: "authorization_code",
      client_id: config.googleClientId,
      code: params.code,
      code_verifier: params.codeVerifier,
      redirect_uri: params.redirectUri,
    }),
  });

  const payload = (await response.json().catch(() => ({}))) as TokenResponse;
  if (!response.ok) {
    const code =
      typeof payload.error === "string" ? payload.error : "token_request_failed";
    throw new OAuthError(code, response.status);
  }
  if (typeof payload.id_token !== "string" || payload.id_token === "") {
    throw new OAuthError("invalid_response", response.status);
  }
  const expiresIn =
    typeof payload.expires_in === "number" ? payload.expires_in : 3600;
  return {
    idToken: payload.id_token,
    expiresAt: Date.now() + expiresIn * 1000,
  };
}
