import { useLocalSearchParams } from 'expo-router';

import { RequireSession } from '@/components/require-session';
import { CreditorMatrixScreen } from '@/screens/creditor-matrix';

/**
 * `/cases/<id>/creditor-matrix` — generate and save the court's creditor
 * mailing matrix (issue #282).
 *
 * The same shape as its siblings: the session guard lives here in `src/app/`
 * where the routes are, and the parameter is narrowed to a plain `string` so
 * the screen stays renderable from a test — see `documents.tsx` for the full
 * argument.
 */
export default function CaseCreditorMatrixRoute() {
  const params = useLocalSearchParams();
  const raw = params.caseId;
  const caseId = Array.isArray(raw) ? (raw[0] ?? '') : (raw ?? '');

  return (
    <RequireSession>
      <CreditorMatrixScreen caseId={caseId} />
    </RequireSession>
  );
}
