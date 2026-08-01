import { usePathname, useRouter } from 'expo-router';
import { useEffect } from 'react';
import type { ReactNode } from 'react';

import { StatusScreen } from '@/components/status-screen';
import { useSession } from '@/session';

export interface RequireSessionProps {
  children: ReactNode;
}

/**
 * The route guard: renders `children` only for a signed-in user, and sends
 * everyone else to sign-in.
 *
 * **The `loading` arm is the acceptance criterion of issue #78, not a nicety.**
 * On every reload the session starts as `loading` while the stored refresh
 * token is exchanged. The two obvious shapes both fail there:
 *
 * - treating "not signed in yet" as signed out bounces an *already signed-in*
 *   user to `/sign-in` for a moment on every single reload;
 * - rendering `children` optimistically flashes protected content — case data —
 *   to someone who may turn out not to have a session at all.
 *
 * So this component renders neither until the status resolves. There is no
 * third state to add later: `loading` ends in `signed-in` or `signed-out`.
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
    return <>{children}</>;
  }

  // Covers both `loading` and the frame or two after `signed-out` before the
  // effect's navigation commits. Deliberately the same screen: the difference
  // is not something a user needs to be told, and a bare `null` here would be
  // a blank page with no heading for axe to find.
  return (
    <StatusScreen
      title="Checking your session"
      message="One moment while we confirm you are signed in."
    />
  );
}
