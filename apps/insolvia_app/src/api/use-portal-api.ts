import { ApiUnauthorizedException, InsolviaApiClient } from '@insolvia-ai/api-client';
import { useRouter } from 'expo-router';
import { useCallback, useMemo } from 'react';

import type { ApiResult } from '@/api/use-api';
import { appEnvironment, environmentInfo } from '@/config/environment';
import { usePortalSession } from '@/session';

/**
 * The API client for the CLIENT PORTAL (ADR 0023): {@link useApi}'s policy,
 * over the portal session's token.
 *
 * A separate hook rather than a parameter on `useApi`, for the same reason
 * the session is a separate provider: `useApi` reads `useSession()` — the
 * STAFF session — and a portal screen must have no way to reach it. A staff
 * token on `/v1/portal/*` answers 401, and a portal token on a staff route
 * answers 401; wiring the wrong session in would fail closed, but as a
 * sign-in loop nobody could explain.
 *
 * The 401 rule is `useApi`'s, unchanged: a `client` 401 (no token held) goes
 * to the portal's sign-in; a `server` 401 earns exactly one refresh and one
 * retry, then the session ends.
 */
export function usePortalApi() {
  const router = useRouter();
  const { accessToken, refresh, signOut } = usePortalSession();

  const client = useMemo(
    () => new InsolviaApiClient(environmentInfo(appEnvironment).apiBaseUrl, { accessToken }),
    [accessToken],
  );

  const call = useCallback(
    async <T>(request: (client: InsolviaApiClient) => Promise<T>): Promise<ApiResult<T>> => {
      try {
        return { ok: true, value: await request(client) };
      } catch (cause) {
        if (!(cause instanceof ApiUnauthorizedException)) {
          throw cause;
        }
        if (cause.source === 'client') {
          router.replace('/portal/sign-in');
          return { ok: false, reason: 'auth' };
        }
      }

      if (!(await refresh())) {
        signOut();
        return { ok: false, reason: 'auth' };
      }
      try {
        return { ok: true, value: await request(client) };
      } catch (cause) {
        if (cause instanceof ApiUnauthorizedException) {
          signOut();
          return { ok: false, reason: 'auth' };
        }
        throw cause;
      }
    },
    [client, refresh, router, signOut],
  );

  return { client, call };
}
