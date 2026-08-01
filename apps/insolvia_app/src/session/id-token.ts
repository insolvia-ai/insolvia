/**
 * Reading display identity out of the ID token.
 *
 * **This is decoding, not verification, and the distinction is the entire
 * reason this file has a header.** Nothing here checks a signature, an issuer,
 * an audience or an expiry. A decoded claim is therefore only ever safe for one
 * thing: putting text on the screen for a user who is already looking at their
 * own session. It must never gate a feature, choose a code path, or be sent
 * anywhere as a statement of who the caller is — ADR 0001's client stays dumb,
 * and the API re-derives identity from the verified access token
 * (`services/api/src/insolvia_api/api/auth.py`).
 *
 * **Why the ID token and not `/v1/me`.** The pool sets
 * `username_attributes = ["email"]`, which makes the access token's `username`
 * claim a Cognito-generated UUID carrying no address at all. `/v1/me` reflects
 * verified access-token claims, so it has no email to return either — by
 * design, per ADR 0007. The ID token is the artifact issued *about* the user,
 * with the `email` claim the `email`/`profile` scopes ask for, and it is the
 * only place the app can read an address from.
 */

import { base64UrlDecode } from '@/session/pkce';

/** The claims this app reads. Everything else in the token is ignored. */
export interface IdTokenClaims {
  /** The user's email address, for display only. `null` when absent. */
  readonly email: string | null;
  /** The `sub` claim — the stable Cognito user id. */
  readonly subject: string | null;
}

const NO_CLAIMS: IdTokenClaims = { email: null, subject: null };

/**
 * Decodes the payload of a JWT and returns the claims used for display.
 *
 * Every failure — absent token, wrong segment count, bad base64url, unparseable
 * JSON, missing claim — collapses to `{ email: null, subject: null }` rather
 * than throwing. The caller's fallback is the same in all of them ("show no
 * address"), and a sign-in that succeeded should not be undone by a
 * cosmetic decode.
 */
export function readIdTokenClaims(idToken: string | null | undefined): IdTokenClaims {
  if (typeof idToken !== 'string' || idToken === '') {
    return NO_CLAIMS;
  }

  // header.payload.signature — the payload is the middle segment. The signature
  // is deliberately not looked at; see this file's header.
  const segments = idToken.split('.');
  if (segments.length !== 3) {
    return NO_CLAIMS;
  }
  const payloadSegment = segments[1];
  if (payloadSegment === undefined || payloadSegment === '') {
    return NO_CLAIMS;
  }

  const bytes = base64UrlDecode(payloadSegment);
  if (bytes === null) {
    return NO_CLAIMS;
  }

  const json = decodeUtf8(bytes);
  if (json === null) {
    return NO_CLAIMS;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    return NO_CLAIMS;
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return NO_CLAIMS;
  }

  const claims = parsed as Record<string, unknown>;
  return {
    email: stringClaim(claims.email),
    subject: stringClaim(claims.sub),
  };
}

function stringClaim(value: unknown): string | null {
  return typeof value === 'string' && value !== '' ? value : null;
}

/**
 * UTF-8 decode, via `TextDecoder` when the runtime has it.
 *
 * `TextDecoder` is present in browsers, in Node, and in Hermes, so the fallback
 * is nearly unreachable — but "nearly" is why this module guards it rather than
 * assuming, the same rule `browser.ts` follows. The fallback handles the ASCII
 * range only, which covers a `sub` UUID and the overwhelming majority of email
 * addresses; anything above it yields `null` and the caller shows no address,
 * which is a better outcome than mojibake in the header.
 */
function decodeUtf8(bytes: Uint8Array): string | null {
  const Decoder = (globalThis as { TextDecoder?: new () => { decode(input: Uint8Array): string } })
    .TextDecoder;
  if (Decoder !== undefined) {
    try {
      return new Decoder().decode(bytes);
    } catch {
      return null;
    }
  }
  let out = '';
  for (const byte of bytes) {
    if (byte > 0x7f) {
      return null;
    }
    out += String.fromCharCode(byte);
  }
  return out;
}
