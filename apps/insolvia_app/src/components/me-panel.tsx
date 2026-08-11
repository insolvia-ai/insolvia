import type { Principal } from '@insolvia-ai/api-client';
import { StyleSheet, Text, View } from 'react-native';

import { useMe } from '@/api/me';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * Renders the session's `GET /v1/me` answer — the proof that the whole
 * authenticated loop closes (issue #77): hosted-UI sign-in → access token in
 * memory → `Authorization: Bearer` → a JWT the API verified against the
 * pool's JWKS → claims on screen.
 *
 * The fetch itself, and the 401 policy that used to be written out here,
 * live in {@link MeProvider} (via `useApi`) since issue #218: the shell's
 * navigation needs the same answer on every screen, so the panel became the
 * home-screen reader of a session-lifetime fact rather than the owner of a
 * per-mount request.
 */
export function MePanel() {
  const theme = useTheme();
  const state = useMe();

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
