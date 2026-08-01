import {
  clearPendingAuthorization,
  clearRefreshToken,
  persistentStore,
  readPendingAuthorization,
  readRefreshToken,
  transientStore,
  writePendingAuthorization,
  writeRefreshToken,
} from '@/session';
import { installFakeBrowser } from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

describe('with no browser storage at all', () => {
  /**
   * jest-expo runs the native environment, where `localStorage` is simply
   * absent — a faithful stand-in for a non-web runtime. Nothing here installs a
   * fake, so this block is the "degrade safely" contract: every call has to
   * answer, and none may throw a `ReferenceError` that takes the app down.
   */
  it('reads as empty instead of throwing', () => {
    expect(persistentStore()).toBeNull();
    expect(readRefreshToken()).toBeNull();
    expect(readPendingAuthorization()).toBeNull();
  });

  it('accepts writes and clears silently', () => {
    expect(() => {
      writeRefreshToken('ignored');
      writePendingAuthorization({ state: 's', codeVerifier: 'v', returnTo: null });
      clearRefreshToken();
      clearPendingAuthorization();
    }).not.toThrow();
  });
});

describe('with browser storage', () => {
  let browser: FakeBrowser;

  beforeEach(() => {
    browser = installFakeBrowser();
  });

  afterEach(() => {
    browser.restore();
  });

  it('puts the refresh token in localStorage and nothing else anywhere', () => {
    writeRefreshToken('test-refresh-token');

    expect(readRefreshToken()).toBe('test-refresh-token');
    // The persisted surface is exactly one entry. Access and ID tokens are
    // memory-only (ADR 0007), and this is the assertion that would fail if a
    // future change started persisting one.
    expect([...browser.localStorage.entries.values()]).toEqual(['test-refresh-token']);
    expect(browser.sessionStorage.entries.size).toBe(0);
  });

  it('replaces the stored token on rotation rather than accumulating', () => {
    writeRefreshToken('first-refresh-token');
    writeRefreshToken('rotated-refresh-token');

    expect(readRefreshToken()).toBe('rotated-refresh-token');
    expect(browser.localStorage.entries.size).toBe(1);
  });

  it('clears the refresh token', () => {
    writeRefreshToken('test-refresh-token');
    clearRefreshToken();

    expect(readRefreshToken()).toBeNull();
    expect(browser.localStorage.entries.size).toBe(0);
  });

  it('keeps the pending attempt in sessionStorage, not localStorage', () => {
    // Per-tab and per-attempt: two tabs signing in at once must not overwrite
    // each other's verifier, and an abandoned attempt must not outlive the tab.
    writePendingAuthorization({
      state: 'test-state',
      codeVerifier: 'test-verifier',
      returnTo: '/somewhere',
    });

    expect(transientStore()).not.toBeNull();
    expect(browser.sessionStorage.entries.size).toBe(1);
    expect(browser.localStorage.entries.size).toBe(0);
    expect(readPendingAuthorization()).toEqual({
      state: 'test-state',
      codeVerifier: 'test-verifier',
      returnTo: '/somewhere',
    });
  });

  it('clears the pending attempt', () => {
    writePendingAuthorization({ state: 's', codeVerifier: 'v', returnTo: null });
    clearPendingAuthorization();

    expect(readPendingAuthorization()).toBeNull();
  });

  it.each([
    ['not JSON at all', 'definitely-not-json'],
    ['JSON that is not an object', '"a string"'],
    ['a record with no state', JSON.stringify({ codeVerifier: 'v' })],
    ['a record with no verifier', JSON.stringify({ state: 's' })],
    ['a record with an empty state', JSON.stringify({ state: '', codeVerifier: 'v' })],
  ])('treats %s as no pending attempt', (_label, stored) => {
    // This value round-trips through storage the user can edit. A verifier that
    // came back silently `undefined` would produce an opaque `invalid_grant` at
    // the token endpoint with nothing to point at; reading as absent instead
    // routes it into the `state` check, which fails loudly.
    browser.sessionStorage.entries.set('insolvia.auth.pending-authorization', stored);

    expect(readPendingAuthorization()).toBeNull();
  });
});

describe('when the store throws on access', () => {
  /** Safari in private mode throws on the property read itself. */
  it('is treated as having no storage', () => {
    const globals = globalThis as { localStorage?: unknown };
    const previous = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');

    Object.defineProperty(globalThis, 'localStorage', {
      configurable: true,
      get() {
        throw new Error('SecurityError: access denied');
      },
    });

    try {
      expect(persistentStore()).toBeNull();
      expect(readRefreshToken()).toBeNull();
      expect(() => {
        writeRefreshToken('ignored');
      }).not.toThrow();
    } finally {
      if (previous === undefined) {
        delete globals.localStorage;
      } else {
        Object.defineProperty(globalThis, 'localStorage', previous);
      }
    }
  });
});
