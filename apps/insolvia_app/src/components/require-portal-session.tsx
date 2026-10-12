import { usePathname, useRouter } from 'expo-router';
import { useEffect } from 'react';
import type { ReactNode } from 'react';

import { PortalShell } from '@/components/portal-shell';
import { StatusScreen } from '@/components/status-screen';
import { usePortalSession } from '@/session';

export interface RequirePortalSessionProps {
  children: ReactNode;
}

/**
 * The client portal's route guard: `children` for a signed-in CLIENT, and
 * `/portal/sign-in` for everyone else (ADR 0023).
 *
 * Simpler than the staff `RequireSession`, because the portal session is
 * memory-only: every page load starts signed out, so there is no optimistic
 * restore to wait on and no `restoring` window. It also composes no
 * `RequireProfile` — that gate asks a FIRM USER for their name through the
 * staff `/v1/me`, and a client's name is the one the firm gave at invitation.
 *
 * The redirect is an effect for `RequireSession`'s reason, and carries the
 * guarded path as `returnTo`, which `safePortalReturnTo` holds inside
 * `/portal` on the way back.
 */
export function RequirePortalSession({ children }: RequirePortalSessionProps) {
  const { status } = usePortalSession();
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    if (status === 'signed-out') {
      router.replace({ pathname: '/portal/sign-in', params: { returnTo: pathname } });
    }
  }, [pathname, router, status]);

  if (status === 'signed-in') {
    return <>{children}</>;
  }
  return (
    <StatusScreen
      defer
      shell={PortalShell}
      title="Taking you to sign-in"
      message="One moment while we take you to the sign-in page."
    />
  );
}
