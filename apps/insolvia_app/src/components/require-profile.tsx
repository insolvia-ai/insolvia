import type { ReactNode } from 'react';

import { useMe } from '@/api/me';
import { CompleteProfile } from '@/screens/complete-profile';

export interface RequireProfileProps {
  children: ReactNode;
}

/**
 * The third guard: a signed-in member of a firm who has no usable name is
 * asked for one before anything else.
 *
 * ## Why anybody is in this state at all
 *
 * A name used to be one free-text string, and it still arrives that way for
 * everyone who was invited before the split. The server derives a first and a
 * last half from it (`split_legacy_name`), and a name it cannot split — a
 * single token — yields an EMPTY SURNAME rather than a guess. That is the
 * honest answer, and this component is what turns it into a question.
 *
 * It is not a migration artefact only. Any future path that creates a member
 * without both halves lands here too, which is the point of gating on the
 * data rather than on a "needs onboarding" flag nobody would remember to set.
 *
 * ## Where it mounts, and why not in the layout
 *
 * Inside {@link RequireSession}'s signed-in arm, NOT around `<Stack>` in
 * `src/app/_layout.tsx`. Two reasons, and the first is mechanical:
 *
 * - A guard that renders a screen INSTEAD of its children would unmount the
 *   navigator, and expo-router throws "Attempted to navigate before mounting
 *   the Root Layout" for any `router.replace` in flight when that happens.
 * - `/sign-in` and `/auth/callback` are excluded STRUCTURALLY — those two route
 *   files compose no guard at all — rather than by a pathname allowlist that
 *   would have to be kept in step with them. That matters more than it looks:
 *   the session flips to `signed-in` DURING the callback exchange, so a
 *   pathname-based gate would race it and could unmount the exchange effect
 *   mid-flight.
 *
 * Every protected route already composes `RequireSession`, so a route added
 * tomorrow is covered for free. `+not-found.tsx` is the one uncovered route,
 * correctly: a page that does not exist should say so rather than ask for a
 * name.
 *
 * ## What it deliberately does not do
 *
 * **It does not block while `/v1/me` is in flight.** Doing so would put every
 * screen in the app behind a round trip on first paint, serialised ahead of
 * whatever that screen fetches for itself. The cost is that somebody in this
 * state may see one frame of the app before the gate appears — once per
 * session, for the rare account it applies to.
 *
 * **It is not a security control**, exactly as `RequireFirm` says of itself. A
 * missing name gates nothing on the server; a bug here costs a prompt, not
 * access.
 */
export function RequireProfile({ children }: RequireProfileProps) {
  const state = useMe();

  if (state.kind !== 'ready') {
    // `loading` — see above. `error` — nothing is proven, and refusing to
    // render the app because a request failed would be a worse answer than
    // letting the screen's own error handling speak.
    return <>{children}</>;
  }

  const membership = state.principal.firm;
  if (membership === undefined) {
    // Signed in, in no firm: there is no row to write a name to, and
    // `RequireFirm` already owns the explanation for that state.
    return <>{children}</>;
  }

  // `trim()` rather than a bare emptiness check: the server rejects a blank
  // name on the way in, so whitespace should be unreachable here — but a value
  // that only LOOKS present is precisely the thing this gate exists to catch.
  if (membership.firstName.trim() === '' || membership.lastName.trim() === '') {
    return <CompleteProfile membership={membership} />;
  }

  return <>{children}</>;
}
