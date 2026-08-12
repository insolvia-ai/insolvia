import type { Principal } from '@insolvia-ai/api-client';
import { Collapsible } from '@insolvia-ai/design-system';
import { StyleSheet, Text, View } from 'react-native';

import { useMe } from '@/api/me';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * The session's `GET /v1/me` answer, collapsed, at the bottom of `/account`.
 *
 * It began as proof that the whole authenticated loop closes (issue #77):
 * hosted-UI sign-in → access token in memory → `Authorization: Bearer` → a JWT
 * the API verified against the pool's JWKS → claims on screen. That proof was
 * worth putting on the HOME screen exactly once, while the pipeline was the
 * product. It is support detail now — a Cognito subject and an app-client id
 * mean nothing to somebody preparing a bankruptcy petition — so it moved here
 * and starts closed.
 *
 * It is not deleted, because the one moment it earns its place is real: a
 * customer on a call who needs to read their subject back to us. Buried and
 * available beats gone.
 *
 * The fetch itself, and the 401 policy that used to be written out here,
 * live in {@link MeProvider} (via `useApi`) since issue #218: the shell's
 * navigation needs the same answer on every screen, so this became a reader of
 * a session-lifetime fact rather than the owner of a per-mount request.
 *
 * The heading sits OUTSIDE the disclosure and the trigger is its own control.
 * A `Heading` inside `Collapsible.Trigger` would be a heading inside a button —
 * the trigger wraps its children in a `Text` — and a heading that only exists
 * while the section is open breaks document structure for anyone navigating by
 * headings.
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
      <Heading level={2}>Support details</Heading>
      <Text style={[styles.line, { color: theme.colors.muted, fontFamily: theme.typography.body }]}>
        What Insolvia support may ask you to read back. Nothing here identifies you to anyone else.
      </Text>

      <Collapsible.Root>
        <Collapsible.Trigger>Show your API session</Collapsible.Trigger>
        {/* Holds a View, which the panel could not do before design-system
            0.16.0 — its native leaf wrapped every child in a `Text`. */}
        <Collapsible.Panel>
          {state.kind === 'ready' ? (
            <PrincipalDetails principal={state.principal} />
          ) : (
            <Text
              aria-live={state.kind === 'error' ? 'assertive' : 'polite'}
              style={[
                styles.line,
                { color: theme.colors.muted, fontFamily: theme.typography.body },
              ]}
            >
              {state.kind === 'loading' ? 'Checking your API session…' : state.message}
            </Text>
          )}
        </Collapsible.Panel>
      </Collapsible.Root>
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
