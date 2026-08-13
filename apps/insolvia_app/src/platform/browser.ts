/**
 * Every browser global the app touches, each behind a guard.
 *
 * **Why this file exists at all.** Web is the only shipping target (decision D9
 * in `docs/plan.md`, [ADR 0004]), so the app is written directly against
 * `location`, `localStorage` and `sessionStorage` rather than against a
 * cross-platform abstraction. But this is a React Native app: the same modules
 * are loaded by jest-expo's native test environment, and would be loaded by a
 * native client if one ever shipped. A bare `localStorage.getItem(...)` throws
 * a `ReferenceError` there and takes the whole app down. Reading every global
 * through this file turns "there is no browser here" into `null`, which every
 * caller already has to handle for the far more common web case: Safari in
 * private mode throws on `localStorage` access, and a user can disable storage
 * outright.
 *
 * **It lives in `platform/`, not `session/`, because it is platform plumbing
 * rather than session state.** It sat under `session/` while the session was
 * its only caller; the colour-scheme preference is the second, and `@/session`
 * is a barrel screens reach only through `useSession()` — so leaving it there
 * would have meant reaching inside another module's public surface for a
 * `localStorage` wrapper.
 *
 * **A future native client is what would justify `expo-auth-session`** — it
 * owns the custom-scheme redirect (`insolvia://auth/callback`), the system
 * browser handoff and the platform secure store, none of which this file can
 * or should grow. Until that client exists it would be a dependency with no
 * consumer, so the PKCE and storage work here is done with Web Crypto and the
 * platform storage APIs directly.
 *
 * Every read is **lazy** — at call time, never at module load. A module-load
 * read would capture `undefined` in the test environment before a fake could be
 * installed, and would miss a polyfill that lands after this module is
 * evaluated.
 *
 * [ADR 0004]: ../../../../docs/adr/0004-react-native-replaces-flutter.md
 */

/**
 * The slice of the DOM `Storage` interface the session uses.
 *
 * Declared structurally rather than as the DOM's `Storage` so this file does
 * not need the DOM lib, which the app's `tsconfig.json` (Expo's base) does not
 * guarantee.
 */
export interface StorageLike {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

interface BrowserGlobals {
  location?: {
    origin?: string;
    href?: string;
    assign?: (url: string) => void;
  };
  localStorage?: StorageLike;
  sessionStorage?: StorageLike;
}

function globals(): BrowserGlobals {
  return globalThis as BrowserGlobals;
}

/**
 * The page's origin (`https://app.insolvia.ai`), or `null` off the web.
 *
 * The origin is load-bearing twice over: it is the base of the OAuth redirect
 * URI, and it is the `logout_uri` on the way out. Cognito matches both
 * **exactly** against the lists `infra/modules/auth/main.tf` registers, so this
 * value is derived from the running page rather than configured — a configured
 * origin that drifted from the deployed one would fail at Cognito with an
 * opaque error.
 */
export function currentOrigin(): string | null {
  const origin = globals().location?.origin;
  return typeof origin === 'string' && origin !== '' ? origin : null;
}

/**
 * Leaves the SPA for a full-page navigation — the hosted UI's `/authorize` on
 * the way in, `/logout` on the way out.
 *
 * Deliberately **not** `router.replace`: the hosted UI is a different origin
 * and the router cannot reach it. A no-op off the web, which is the honest
 * answer when there is no browser to navigate.
 */
export function navigateTo(url: string): void {
  const location = globals().location;
  if (location === undefined) {
    return;
  }
  if (typeof location.assign === 'function') {
    location.assign(url);
    return;
  }
  location.href = url;
}

/**
 * `localStorage` — the **persistent** store, and the only thing in this app
 * that survives a tab closing.
 *
 * Two values live here. The **refresh token** is ADR 0007's eyes-open
 * trade-off: it buys the 30-day "stay signed in" window the pool is configured
 * for, at the cost that a successful XSS can read it. Access and ID tokens
 * never come near it. The **colour-scheme preference** is the other, and it is
 * not sensitive — it is here because it has to survive a reload to be worth
 * having, and because reading it synchronously at first paint is what stops a
 * flash of the wrong scheme.
 */
export function persistentStore(): StorageLike | null {
  return usableStore(() => globals().localStorage);
}

/**
 * `sessionStorage` — the **transient** store, scoped to this tab and cleared
 * when it closes.
 *
 * It carries the CSRF `state` and the PKCE `code_verifier` across the redirect
 * to the hosted UI and back. Per-tab is the correct scope: two tabs signing in
 * at once must not overwrite each other's verifier.
 */
export function transientStore(): StorageLike | null {
  return usableStore(() => globals().sessionStorage);
}

/**
 * Returns the store only if it can actually be used.
 *
 * Merely *reading* `localStorage` throws in Safari's private mode, so the
 * access itself is inside the `try`. A probe write is deliberately not done
 * here — it would be a side effect on every read, and a store that accepts
 * `setItem` and silently discards it (iOS private mode, historically) is
 * handled where it matters: the session treats a missing refresh token as
 * "signed out", which is exactly what such a store produces.
 */
function usableStore(read: () => StorageLike | undefined): StorageLike | null {
  try {
    const store = read();
    if (store === undefined || store === null || typeof store.getItem !== 'function') {
      return null;
    }
    return store;
  } catch {
    return null;
  }
}

/**
 * Reads a key, treating an unavailable or throwing store as "absent".
 *
 * The three helpers below exist so callers never repeat the null check and the
 * `try`. A storage quota error on `setItem` must not break sign-in.
 */
export function readFrom(store: StorageLike | null, key: string): string | null {
  if (store === null) {
    return null;
  }
  try {
    return store.getItem(key);
  } catch {
    return null;
  }
}

/** Writes a key, ignoring an unavailable store or a quota failure. */
export function writeTo(store: StorageLike | null, key: string, value: string): void {
  if (store === null) {
    return;
  }
  try {
    store.setItem(key, value);
  } catch {
    // A session that cannot persist still works for the life of the page.
  }
}

/** Removes a key. Never throws — clearing state must not be able to fail. */
export function removeFrom(store: StorageLike | null, key: string): void {
  if (store === null) {
    return;
  }
  try {
    store.removeItem(key);
  } catch {
    // Nothing to do: the value is already unreachable to us.
  }
}
