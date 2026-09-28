import { PortalAuthCallback } from '@/screens/portal/auth-callback';

/**
 * `/portal/auth/callback` — the portal's OAuth return leg.
 *
 * **This path must stay in step with `infra/modules/auth/main.tf`**, whose
 * portal client registers `<origin>/portal/auth/callback` as its only
 * callback URL (`portal_callback_urls`). Under file-based routing the path IS
 * this file's location; `portal-routes.test.tsx` pins it. Composes no guard:
 * the session becomes signed-in during the exchange this route performs.
 */
export default function PortalAuthCallbackRoute() {
  return <PortalAuthCallback />;
}
