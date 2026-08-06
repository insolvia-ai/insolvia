import { useLocalSearchParams } from 'expo-router';

import { RequireFirm } from '@/components/require-firm';
import { RequireSession } from '@/components/require-session';
import { Team } from '@/screens/team';

/**
 * `/cases/<id>/team` — who is linked to a case.
 *
 * Same shape as the sibling routes: the guards live here, the parameter is
 * narrowed here, and the screen stays a screen. `RequireFirm` is the second
 * guard because assignment is entirely about the caller's firm — see that
 * component for why "signed in" and "in a firm" are separate states.
 */
export default function CaseTeamRoute() {
  const params = useLocalSearchParams();
  const raw = params.caseId;
  const caseId = Array.isArray(raw) ? (raw[0] ?? '') : (raw ?? '');

  return (
    <RequireSession>
      <RequireFirm>{(membership) => <Team caseId={caseId} membership={membership} />}</RequireFirm>
    </RequireSession>
  );
}
