import { Slot } from 'expo-router';

import { PortalSessionProvider } from '@/session';

/**
 * `/portal/*` — the client portal's route group (ADR 0023).
 *
 * **A path segment, not a `(portal)` group directory.** The ADR calls it the
 * `(portal)` route group, and in the sense that matters — its own layout,
 * session, guard and frame — it is one. But Expo Router's parenthesised
 * groups add NO URL segment, and the portal's URLs are pinned by infra:
 * `infra/modules/auth/main.tf` registers `<origin>/portal/auth/callback` as
 * the portal app client's only callback and `<origin>/portal` as its only
 * logout URI, and the invitation mail links to `<app origin>/portal`. So the
 * directory is `portal/`, and the URLs are what infra says they are.
 *
 * **Its own session provider, mounted here, once.** `PortalSessionProvider`
 * (memory-only; its file owns the argument) wraps every portal route —
 * including `/portal/sign-in` and `/portal/auth/callback`, which need the
 * session but compose no guard — and survives navigation between them,
 * because this layout does. The root layout's STAFF session still sits above
 * it; it is inert for a debtor (no stored staff token, so it never makes a
 * request), and nothing under this directory reads it: portal screens use
 * `usePortalSession`, `usePortalApi` and `PortalShell`, never the staff
 * `useSession`, `useApi` or `AppShell`.
 *
 * `Slot` rather than a nested `Stack`: the root `Stack` already provides the
 * navigator (headerless), and a second one would only add a second history
 * for three screens that replace one another.
 */
export default function PortalLayout() {
  return (
    <PortalSessionProvider>
      <Slot />
    </PortalSessionProvider>
  );
}
