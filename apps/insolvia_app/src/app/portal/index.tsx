import { RequirePortalSession } from '@/components/require-portal-session';
import { PortalHome } from '@/screens/portal/home';

/**
 * `/portal` — the client's landing screen, behind the portal's own guard.
 * Also the portal app client's registered sign-out landing, and the URL the
 * invitation mail links to.
 */
export default function PortalHomeRoute() {
  return (
    <RequirePortalSession>
      <PortalHome />
    </RequirePortalSession>
  );
}
