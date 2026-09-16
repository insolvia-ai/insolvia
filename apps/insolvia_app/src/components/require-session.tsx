import { usePathname, useRouter } from 'expo-router';
import { useEffect } from 'react';
import type { ReactNode } from 'react';

import { RequireProfile } from '@/components/require-profile';
import { StatusScreen } from '@/components/status-screen';
import { useSession } from '@/session';

export interface RequireSessionProps {
  children: ReactNode;
}

/**
 * The route guard: renders `children` only for a signed-in user with a usable
 * name, and sends everyone else to sign-in.
 *
 * **It never blocks on the session being confirmed.** A reload with a stored
 * refresh token starts `signed-in` while the token is exchanged in the
 * background (`SessionProvider`'s header has the argument). Issue #78 wanted
 * two things here, and both still hold:
 *
 * - an *already signed-in* user is never bounced to `/sign-in` for a moment
 *   on reload — the status is `signed-in` from the first frame;
 * - protected content — case data — never flashes at someone who turns out
 *   to have no session — every fetch waits on `accessToken()`, so nothing
 *   case-shaped renders until Cognito has answered, and a failed exchange
 *   lands in `signed-out` and the redirect below.
 *
 * What the user with a stale token sees is the app's chrome and a screen's
 * loading skeleton for the length of one round trip, then sign-in. That is
 * the trade for every other reload painting nothing in between.
 *
 * The redirect is an **effect**, not something computed during render, because
 * navigating is a side effect and `router.replace` during render is a React
 * error. `replace` rather than `push` so the protected URL does not sit in
 * history behind the sign-in screen.
 *
 * The path being guarded rides along as `returnTo`, so a user who asked for a
 * deep link lands back on it rather than on `/` after signing in. It is a query
 * parameter and therefore attacker-controlled — `safeReturnTo` in the session
 * provider is what stops it becoming an open redirect.
 */
export function RequireSession({ children }: RequireSessionProps) {
  const { status } = useSession();
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    if (status !== 'signed-out') {
      return;
    }
    router.replace({ pathname: '/sign-in', params: { returnTo: pathname } });
  }, [pathname, router, status]);

  if (status === 'signed-in') {
    // {@link RequireProfile} sits HERE rather than around the navigator in
    // `src/app/_layout.tsx` — it owns the reasoning, and the short version is
    // that a guard replacing its children would unmount the `<Stack>`. Every
    // protected route composes this component, so putting it here covers all
    // of them and structurally spares `/sign-in` and `/auth/callback`, which
    // compose no guard at all.
    return <RequireProfile>{children}</RequireProfile>;
  }

  // `signed-out`: the frame or two before the effect's navigation commits.
  // Deferred, so that frame is an empty shell rather than a titled page that
  // flickers past — and not a bare `null`, so a navigation that stalls still
  // ends up with a heading for axe to find.
  return (
    <StatusScreen
      defer
      title="Taking you to sign-in"
      message="One moment while we take you to the sign-in page."
    />
  );
}
