/**
 * PKCE (RFC 7636) and the base64url codec it needs, over Web Crypto.
 *
 * **This file is the only thing that makes the sign-in flow PKCE-protected.**
 * Cognito has no server-side "require PKCE" toggle — `infra/modules/auth/main.tf`
 * says so in its own comment, and ADR 0007 repeats it — so the authorize
 * endpoint honours a `code_challenge` when one is sent and cannot insist that
 * one is. Nothing in the infrastructure will fail if these parameters stop
 * being generated; only `pkce.test.ts` and `oauth.test.ts` will.
 *
 * No dependency does this work. `expo-auth-session` would, and is the right
 * answer the day a native client needs a custom-scheme redirect and a system
 * browser handoff (see `browser.ts`), but on web the whole of PKCE is a random
 * string, a SHA-256, and a base64url encode — all three of which are already in
 * the platform.
 */

/**
 * The base64url alphabet (RFC 4648 §5): base64 with `+/` swapped for `-_`, and
 * no `=` padding, which is exactly the `code_challenge` encoding RFC 7636 §4.2
 * requires.
 */
const BASE64URL_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';

/**
 * 32 bytes → a 43-character verifier, the shortest RFC 7636 §4.1 allows (the
 * legal range is 43–128) and a full 256 bits of entropy.
 */
const VERIFIER_BYTES = 32;

/** The subset of Web Crypto this module needs. */
interface WebCryptoLike {
  getRandomValues<T extends Uint8Array>(array: T): T;
  subtle: { digest(algorithm: string, data: Uint8Array): Promise<ArrayBuffer> };
}

/**
 * Raised when the runtime has no usable Web Crypto.
 *
 * A distinct type because the caller's only correct response is to refuse to
 * start sign-in and say so. **There is deliberately no `Math.random` fallback:**
 * the verifier and the `state` are security parameters, and a predictable one is
 * worse than a sign-in button that reports it cannot proceed — it looks like it
 * worked.
 */
export class CryptoUnavailableError extends Error {
  constructor() {
    super('this runtime has no Web Crypto, so sign-in cannot be started securely');
    this.name = 'CryptoUnavailableError';
  }
}

/**
 * Web Crypto, or `null`. Read lazily, for the same reason `browser.ts` reads
 * its globals lazily.
 *
 * `crypto.subtle` is only exposed on **secure contexts** in a browser — HTTPS
 * or `localhost` — which is a constraint the app already meets everywhere it
 * runs (`infra/modules/web_hosting` is HTTPS-only; the dev server is
 * `http://localhost:3000`, which counts as secure).
 */
function webCrypto(): WebCryptoLike | null {
  const candidate = (globalThis as { crypto?: Partial<WebCryptoLike> }).crypto;
  if (
    candidate === undefined ||
    typeof candidate.getRandomValues !== 'function' ||
    typeof candidate.subtle?.digest !== 'function'
  ) {
    return null;
  }
  return candidate as WebCryptoLike;
}

/**
 * Encodes bytes as unpadded base64url.
 *
 * Hand-rolled rather than `btoa(...)` + three `replace`s: `btoa` is a DOM
 * global that bare React Native does not guarantee, and the binary-string dance
 * it needs is more code than the encoder itself. `charAt` rather than `[i]`
 * because `noUncheckedIndexedAccess` types the latter `string | undefined`.
 */
export function base64UrlEncode(bytes: Uint8Array): string {
  let out = '';
  for (let i = 0; i < bytes.length; i += 3) {
    const b0 = bytes[i] ?? 0;
    const b1 = bytes[i + 1];
    const b2 = bytes[i + 2];

    out += BASE64URL_ALPHABET.charAt(b0 >> 2);
    out += BASE64URL_ALPHABET.charAt(((b0 & 0x03) << 4) | ((b1 ?? 0) >> 4));
    if (b1 === undefined) break;

    out += BASE64URL_ALPHABET.charAt(((b1 & 0x0f) << 2) | ((b2 ?? 0) >> 6));
    if (b2 === undefined) break;

    out += BASE64URL_ALPHABET.charAt(b2 & 0x3f);
  }
  return out;
}

/**
 * Decodes unpadded base64url back to bytes, or `null` if the input is not
 * valid base64url.
 *
 * `null` rather than a throw: the only caller decodes a JWT payload that came
 * over the network, where "malformed" is an ordinary outcome to render around,
 * not an exception to unwind through. Standard base64's `+/` and trailing `=`
 * padding are tolerated, since a caller may hand this either spelling.
 */
export function base64UrlDecode(value: string): Uint8Array | null {
  const normalized = value.replace(/-/g, '+').replace(/_/g, '/').replace(/=+$/, '');
  const remainder = normalized.length % 4;
  // A single leftover character cannot encode even one byte.
  if (remainder === 1) {
    return null;
  }

  const bytes: number[] = [];
  let accumulator = 0;
  let bitsHeld = 0;

  for (const character of normalized) {
    const value6 = BASE64URL_ALPHABET.indexOf(
      character === '+' ? '-' : character === '/' ? '_' : character,
    );
    if (value6 < 0) {
      return null;
    }
    accumulator = (accumulator << 6) | value6;
    bitsHeld += 6;
    if (bitsHeld >= 8) {
      bitsHeld -= 8;
      bytes.push((accumulator >> bitsHeld) & 0xff);
    }
  }

  return Uint8Array.from(bytes);
}

/**
 * A cryptographically random, URL-safe token.
 *
 * Used for both the PKCE `code_verifier` and the OAuth `state`. Throws
 * {@link CryptoUnavailableError} rather than degrading — see that type.
 */
export function randomUrlSafeToken(byteLength: number = VERIFIER_BYTES): string {
  const crypto = webCrypto();
  if (crypto === null) {
    throw new CryptoUnavailableError();
  }
  return base64UrlEncode(crypto.getRandomValues(new Uint8Array(byteLength)));
}

/** A fresh PKCE `code_verifier`: 43 base64url characters, 256 bits of entropy. */
export function createCodeVerifier(): string {
  return randomUrlSafeToken(VERIFIER_BYTES);
}

/**
 * Derives the S256 `code_challenge` from a verifier:
 * `BASE64URL(SHA256(ASCII(verifier)))`, per RFC 7636 §4.2.
 *
 * The verifier is encoded by `charCodeAt` rather than `TextEncoder` because
 * {@link createCodeVerifier} draws it from the base64url alphabet, so every
 * character is ASCII by construction — and that removes a second global this
 * module would otherwise have to guard. A character outside ASCII would be a
 * caller error, and is masked to a byte rather than silently mis-encoded.
 */
export async function deriveCodeChallenge(verifier: string): Promise<string> {
  const crypto = webCrypto();
  if (crypto === null) {
    throw new CryptoUnavailableError();
  }
  const ascii = new Uint8Array(verifier.length);
  for (let i = 0; i < verifier.length; i += 1) {
    ascii[i] = verifier.charCodeAt(i) & 0xff;
  }
  const digest = await crypto.subtle.digest('SHA-256', ascii);
  return base64UrlEncode(new Uint8Array(digest));
}

/** The `code_challenge_method` this app sends. Never `plain`. */
export const CODE_CHALLENGE_METHOD = 'S256';

/** A verifier and the challenge derived from it, generated together. */
export interface PkcePair {
  readonly verifier: string;
  readonly challenge: string;
}

/**
 * Creates a matched verifier/challenge pair.
 *
 * One call so the two can never drift: the verifier is stored and the challenge
 * is sent, and a mismatch is only discoverable at the token endpoint, as an
 * `invalid_grant` with nothing to point at.
 */
export async function createPkcePair(): Promise<PkcePair> {
  const verifier = createCodeVerifier();
  return { verifier, challenge: await deriveCodeChallenge(verifier) };
}
