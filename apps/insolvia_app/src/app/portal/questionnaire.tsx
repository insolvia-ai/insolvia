import { RequirePortalSession } from '@/components/require-portal-session';
import { PortalQuestionnaireScreen } from '@/screens/portal/questionnaire';

/**
 * `/portal/questionnaire?section=<id>` — the client's questionnaire, one
 * section at a time, behind the portal's own guard (ADR 0023 PR 4).
 */
export default function PortalQuestionnaireRoute() {
  return (
    <RequirePortalSession>
      <PortalQuestionnaireScreen />
    </RequirePortalSession>
  );
}
