import { RequireFirm } from '@/components/require-firm';
import { useCase } from '@/components/case-shell';
import { Team } from '@/screens/team';

/**
 * `/cases/<id>/team` — who is linked to a case.
 *
 * The session guard and the `caseId` narrowing moved up to `_layout.tsx`.
 * `RequireFirm` stays here, and only here: assignment is entirely about the
 * caller's firm, and "signed in" and "in a firm" are separate states — see that
 * component. Hoisting it to the layout would make every case screen demand a
 * firm to render, which is a stronger claim than any of the others needs.
 */
export default function CaseTeamRoute() {
  const { caseId } = useCase();

  return (
    <RequireFirm>{(membership) => <Team caseId={caseId} membership={membership} />}</RequireFirm>
  );
}
