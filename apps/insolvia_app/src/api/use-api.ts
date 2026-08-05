import { ApiUnauthorizedException, InsolviaApiClient } from '@insolvia-ai/api-client';
import { useRouter } from 'expo-router';
import { useCallback, useMemo } from 'react';

import { appEnvironment, environmentInfo } from '@/config/environment';
import { useSession } from '@/session';

/**
 * What a call ended up being.
 *
 * `ok: false` means the session ended: we have already navigated to sign-in or
 * signed out, so the screen should stop rather than render an error. It is the
 * ONLY failure this type models — every other failure is rethrown, because a
 * 400 carrying per-field messages and a network outage need different handling
 * and flattening them into one "failed" would throw the field errors away.
 */
export type ApiResult<T> =
  | { readonly ok: true; readonly value: T }
  | { readonly ok: false; readonly reason: 'auth' };

/**
 * The authenticated API client, plus the one 401 policy every screen shares.
 *
 * This exists because that policy is subtle and was previously written out
 * inside `MePanel`. A second screen calling the API is exactly when a
 * hand-copied version starts to drift, and the two halves drifting is the bug
 * nobody notices until a session silently stops refreshing.
 *
 * The policy, unchanged from the one `MePanel` documented (ADR 0007):
 *
 * - **`source: 'client'`** — no token was held, so no request was made. There
 *   is nothing to refresh and retrying is pure latency. Go to sign-in.
 * - **`source: 'server'`** — the API rejected the token. Worth **exactly one**
 *   refresh and one retry. One, not a loop: with refresh-token rotation a
 *   second attempt presents a token the first already retired, and an
 *   unbounded retry against a revoked session hammers the token endpoint
 *   until Cognito throttles. If the retry fails too, the session is finished.
 *
 * The client is built from the session's `accessToken` **provider** rather
 * than a token value — the seam `@insolvia-ai/api-client` exposes for this. It
 * is consulted once per request, so a token refreshed between calls is picked
 * up without rebuilding anything or holding a stale copy.
 */
export function useApi() {
  const router = useRouter();
  const { accessToken, refresh, signOut } = useSession();

  const client = useMemo(
    () => new InsolviaApiClient(environmentInfo(appEnvironment).apiBaseUrl, { accessToken }),
    [accessToken],
  );

  const call = useCallback(
    async <T,>(request: (client: InsolviaApiClient) => Promise<T>): Promise<ApiResult<T>> => {
      try {
        return { ok: true, value: await request(client) };
      } catch (cause) {
        if (!(cause instanceof ApiUnauthorizedException)) {
          throw cause;
        }
        if (cause.source === 'client') {
          router.replace('/sign-in');
          return { ok: false, reason: 'auth' };
        }
      }

      // Server 401: one refresh, one retry, then give up for good.
      if (!(await refresh())) {
        signOut();
        return { ok: false, reason: 'auth' };
      }
      try {
        return { ok: true, value: await request(client) };
      } catch {
        signOut();
        return { ok: false, reason: 'auth' };
      }
    },
    [client, refresh, router, signOut],
  );

  return { client, call };
}
