/**
 * The session module's public surface — import `@/session`, never a file inside it.
 *
 * The directory splits by concern so each piece can be read and tested alone:
 *
 * | File | Owns |
 * |---|---|
 * | `browser.ts` | every browser global, behind a guard |
 * | `pkce.ts` | PKCE (RFC 7636) and the base64url codec, over Web Crypto |
 * | `oauth.ts` | the three hosted-UI endpoints |
 * | `token-store.ts` | what is written down, and where |
 * | `id-token.ts` | display identity, decoded — never verified |
 * | `session-provider.tsx` | the React context tying them together |
 *
 * Screens and components need only `useSession()`. The rest is exported for the
 * one legitimate outside consumer — the tests — and because a module whose
 * internals are unreachable is harder to reason about than one whose surface is
 * simply written down.
 */

export { SessionProvider, useSession, safeReturnTo } from './session-provider';
export type {
  CallbackParams,
  CompleteSignInResult,
  SessionContextValue,
  SessionProviderProps,
  SessionStatus,
  SessionUser,
} from './session-provider';

export {
  authorizeUrl,
  callbackUrlFor,
  exchangeCodeForTokens,
  logoutUrl,
  refreshTokens,
  OAuthError,
  OAUTH_SCOPES,
  CALLBACK_PATH,
} from './oauth';
export type { AuthorizeUrlParams, CodeExchangeParams, FetchLike, TokenSet } from './oauth';

export {
  base64UrlDecode,
  base64UrlEncode,
  createCodeVerifier,
  createPkcePair,
  deriveCodeChallenge,
  randomUrlSafeToken,
  CryptoUnavailableError,
  CODE_CHALLENGE_METHOD,
} from './pkce';
export type { PkcePair } from './pkce';

export { readIdTokenClaims } from './id-token';
export type { IdTokenClaims } from './id-token';

export {
  clearPendingAuthorization,
  clearRefreshToken,
  readPendingAuthorization,
  readRefreshToken,
  writePendingAuthorization,
  writeRefreshToken,
} from './token-store';
export type { PendingAuthorization } from './token-store';

export { currentOrigin, navigateTo, persistentStore, transientStore } from '@/platform/browser';
export type { StorageLike } from '@/platform/browser';
