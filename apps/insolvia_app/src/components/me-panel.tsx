import { ApiUnauthorizedException, InsolviaApiClient } from '@insolvia-ai/api-client';
import type { Principal } from '@insolvia-ai/api-client';
import { useRouter } from 'expo-router';
import { useEffect, useMemo, useRef, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { Heading } from '@/components/heading';
import { appEnvironment, environmentInfo } from '@/config/environment';
import { useSession } from '@/session';
import { fontSizes, spacing, useTheme } from '@/theme';

type PanelState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly principal: Principal }
  | { readonly kind: 'error'; readonly message: string };

/**
 * Calls `GET /v1/me` and renders what came back — the proof that the whole
 * authenticated loop closes (issue #77): hosted-UI sign-in → access token in
 * memory → `Authorization: Bearer` → a JWT the API verified against the pool's
 * JWKS → claims on screen.
 *
 * ## The 401 policy, which is the interesting part
 *
 * `ApiUnauthorizedException` carries a `source`, and the two values mean
 * genuinely different things:
 *
 * - **`client`** — the client had no token and never made a request. There is
 *   nothing to refresh, so retrying is pure latency. Go to sign-in.
 * - **`server`** — the API rejected the token. That is worth **exactly one**
 *   refresh and one retry. One, not a loop: with refresh-token rotation on,
 *   a second attempt would present a token the first had already retired, and
 *   an unbounded retry against a revoked session is a way to hammer the token
 *   endpoint until Cognito throttles. If the retry also fails, the session is
 *   genuinely finished — sign out, both legs.
 *
 * The client is built from the session's `accessToken` **provider**, not from a
 * token value, which is the seam `@insolvia-ai/api-client` exposes for exactly
 * this: it is consulted once per request, so a token refreshed between calls is
 * picked up with no rebuilding and no stale copy held here.
 */
export function MePanel() {
  const theme = useTheme();
  const router = useRouter();
  const { accessToken, refresh, signOut } = useSession();
  const [state, setState] = useState<PanelState>({ kind: 'loading' });

  const client = useMemo(
    () => new InsolviaApiClient(environmentInfo(appEnvironment).apiBaseUrl, { accessToken }),
    [accessToken],
  );

  // The whole point of this panel is one request; without this it would refire
  // on every state change it causes.
  const started = useRef(false);

  useEffect(() => {
    if (started.current) {
      return;
    }
    started.current = true;

    let cancelled = false;
    const settle = (next: PanelState) => {
      if (!cancelled) {
        setState(next);
      }
    };

    const load = async () => {
      try {
        settle({ kind: 'ready', principal: await client.me() });
        return;
      } catch (cause) {
        if (!(cause instanceof ApiUnauthorizedException)) {
          settle({ kind: 'error', message: 'Could not reach the Insolvia API.' });
          return;
        }
        if (cause.source === 'client') {
          // No token to refresh. Nothing to retry.
          router.replace('/sign-in');
          return;
        }
      }

      // Server 401: one refresh, one retry, then give up for good.
      if (!(await refresh())) {
        signOut();
        return;
      }
      try {
        settle({ kind: 'ready', principal: await client.me() });
      } catch {
        signOut();
      }
    };

    void load();
    return () => {
      cancelled = true;
    };
  }, [client, refresh, router, signOut]);

  return (
    <View style={styles.panel}>
      {/*
        level={2}: the screen owns the one <h1>, and a heading chosen for looks
        rather than structure is what produces a `heading-order` failure.
      */}
      <Heading level={2}>Your API session</Heading>
      {state.kind === 'ready' ? (
        <PrincipalDetails principal={state.principal} />
      ) : (
        <Text
          aria-live={state.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.line, { color: theme.colors.muted, fontFamily: theme.typography.body }]}
        >
          {state.kind === 'loading' ? 'Checking your API session…' : state.message}
        </Text>
      )}
    </View>
  );
}

/**
 * The verified claims, each labelled for what it actually is.
 *
 * **`username` is deliberately absent.** It is a Cognito UUID, and putting a
 * UUID next to anything resembling an account label invites a reader to take it
 * for an identifier they recognise. The address is rendered once, by
 * `AccountBar`, from the ID token — see that file.
 */
function PrincipalDetails({ principal }: { principal: Principal }) {
  const theme = useTheme();
  const rows: readonly (readonly [string, string])[] = [
    ['Cognito subject', principal.subject],
    ['App client', principal.clientId],
    ['Scopes', principal.scopes.length === 0 ? 'none' : principal.scopes.join(', ')],
  ];

  return (
    <View style={styles.rows}>
      {rows.map(([label, value]) => (
        <Text
          key={label}
          style={[styles.line, { color: theme.colors.muted, fontFamily: theme.typography.body }]}
        >
          {label}: {value}
        </Text>
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  line: {
    fontSize: fontSizes.label,
  },
  panel: {
    gap: spacing.sm,
    marginTop: spacing.lg,
  },
  rows: {
    gap: spacing.xs,
  },
});
