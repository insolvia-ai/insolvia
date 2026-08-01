/**
 * Test-only helpers for the session. **Not imported by any shipping code**, and
 * not a `*.test.ts` file, so Jest does not collect it as a suite and Metro never
 * reaches it from the app entry.
 *
 * It exists because the session's tests all need the same four things — a
 * browser that has storage, a browser that has an origin, a token endpoint that
 * does not exist, and a JWT — and reimplementing them per file is how the
 * fixtures drift apart.
 *
 * **Every value here is obviously fake.** This repository is public: no real
 * pool id, client id, hosted domain, token, or address appears in it. The
 * domain and email use the reserved `.test` TLD (RFC 6761), which can never be
 * registered.
 */

import type { AuthConfig } from '@/config/environment';
import { base64UrlEncode } from '@/session/pkce';

/** A hosted-UI configuration that cannot resolve to anything real. */
export const TEST_AUTH_CONFIG: AuthConfig = {
  domain: 'https://insolvia-test.auth.example.test',
  clientId: 'test-client-id-0000000000',
};

/** The origin the fake browser reports. Registered nowhere. */
export const TEST_ORIGIN = 'https://app.example.test';

/** An obviously fake address, on a TLD reserved by RFC 6761. */
export const TEST_EMAIL = 'attorney@example.test';

/** An in-memory `Storage`, with the map exposed so a test can assert on it. */
export interface FakeStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
  readonly entries: Map<string, string>;
}

export function createFakeStorage(): FakeStorage {
  const entries = new Map<string, string>();
  return {
    entries,
    getItem: (key) => entries.get(key) ?? null,
    setItem: (key, value) => {
      entries.set(key, value);
    },
    removeItem: (key) => {
      entries.delete(key);
    },
  };
}

/** What {@link installFakeBrowser} hands back. */
export interface FakeBrowser {
  readonly localStorage: FakeStorage;
  readonly sessionStorage: FakeStorage;
  /** Every URL passed to a full-page navigation, in order. */
  readonly navigations: string[];
  /** Restores whatever was on `globalThis` before. */
  restore(): void;
}

interface MutableGlobals {
  localStorage?: unknown;
  sessionStorage?: unknown;
  location?: unknown;
}

/**
 * Installs `localStorage`, `sessionStorage` and a `location` on `globalThis`.
 *
 * jest-expo runs the **native** environment, where `localStorage` is absent
 * entirely and `location` is undefined — which is a faithful stand-in for a
 * non-web runtime, and exactly why `browser.ts` reads every global lazily and
 * behind a guard. A test that wants the web path has to ask for it, here.
 *
 * `location.assign` records instead of navigating: a real assignment would try
 * to leave the page.
 */
export function installFakeBrowser(origin: string = TEST_ORIGIN): FakeBrowser {
  const globals = globalThis as MutableGlobals;
  const previous = {
    localStorage: globals.localStorage,
    sessionStorage: globals.sessionStorage,
    location: globals.location,
  };

  const localStorage = createFakeStorage();
  const sessionStorage = createFakeStorage();
  const navigations: string[] = [];

  globals.localStorage = localStorage;
  globals.sessionStorage = sessionStorage;
  globals.location = {
    origin,
    href: `${origin}/`,
    assign: (url: string) => {
      navigations.push(url);
    },
  };

  return {
    localStorage,
    sessionStorage,
    navigations,
    restore: () => {
      globals.localStorage = previous.localStorage;
      globals.sessionStorage = previous.sessionStorage;
      globals.location = previous.location;
    },
  };
}

/**
 * A JWT-shaped string with a real base64url payload and a **fake signature**.
 *
 * Nothing in the app verifies an ID token signature — see `id-token.ts` — so a
 * decodable payload is the entire contract this has to satisfy.
 */
export function fakeJwt(claims: Record<string, unknown>): string {
  const encode = (value: unknown) =>
    base64UrlEncode(new TextEncoder().encode(JSON.stringify(value)));
  return `${encode({ alg: 'RS256', typ: 'JWT' })}.${encode(claims)}.not-a-real-signature`;
}

/** Options for {@link tokenEndpointResponse}. */
export interface TokenResponseOptions {
  readonly accessToken?: string;
  readonly idToken?: string | null;
  readonly refreshToken?: string | null;
  readonly expiresIn?: number;
}

/**
 * A successful `/oauth2/token` body, shaped exactly as Cognito's.
 *
 * The token *values* are placeholder strings, not JWTs: the code under test
 * treats an access token as an opaque bearer credential, and only the ID token
 * is ever decoded.
 */
export function tokenEndpointResponse(options: TokenResponseOptions = {}): Response {
  const body: Record<string, unknown> = {
    access_token: options.accessToken ?? 'test-access-token',
    token_type: 'Bearer',
    expires_in: options.expiresIn ?? 3600,
  };
  const idToken = options.idToken === undefined ? fakeJwt({ email: TEST_EMAIL }) : options.idToken;
  if (idToken !== null) {
    body.id_token = idToken;
  }
  const refreshToken =
    options.refreshToken === undefined ? 'test-refresh-token' : options.refreshToken;
  if (refreshToken !== null) {
    body.refresh_token = refreshToken;
  }
  return jsonResponse(200, body);
}

/**
 * A `GET /v1/me` success body, in the API's exact wire shape
 * (`services/api/src/insolvia_api/api/routes/me.py`).
 *
 * `username` is a UUID and carries **no email**, because the pool sets
 * `username_attributes = ["email"]` — the property the app's display identity
 * has to work around, so the fixture reproduces it rather than smoothing it
 * over.
 */
export function principalResponse(): Response {
  return jsonResponse(200, {
    subject: '00000000-0000-4000-8000-000000000001',
    username: '00000000-0000-4000-8000-000000000001',
    clientId: TEST_AUTH_CONFIG.clientId,
    scopes: ['openid', 'email', 'profile'],
    expiresAt: null,
  });
}

/** An OAuth error body, e.g. the `invalid_grant` a retired refresh token gets. */
export function tokenEndpointError(code: string = 'invalid_grant', status: number = 400): Response {
  return jsonResponse(status, { error: code });
}

/**
 * A `fetch` stand-in that dispatches on a URL fragment.
 *
 * A route-level test drives two very different endpoints at once — the hosted
 * UI's `/oauth2/token` and the API's `/v1/me` — and a single `mockResolvedValue`
 * cannot tell them apart. An unmatched URL **rejects loudly** rather than
 * returning something plausible: a silent default is how a test ends up
 * asserting against a response nothing under test actually asked for.
 *
 * Deliberately free of `jest`, so this module stays a plain helper.
 */
export function routeFetch(
  handlers: Readonly<Record<string, () => Response>>,
): (url: string) => Promise<Response> {
  return (url: string) => {
    for (const [fragment, respond] of Object.entries(handlers)) {
      if (url.includes(fragment)) {
        return Promise.resolve(respond());
      }
    }
    return Promise.reject(new Error(`unexpected request to ${url}`));
  };
}

/**
 * A minimal `Response`.
 *
 * Built by hand rather than with the global `Response` so the shape is the same
 * whatever the runtime provides, and because only `ok`, `status` and `text()`
 * are ever read.
 */
export function jsonResponse(status: number, body: unknown): Response {
  const text = JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    text: () => Promise.resolve(text),
    json: () => Promise.resolve(body),
  } as unknown as Response;
}
