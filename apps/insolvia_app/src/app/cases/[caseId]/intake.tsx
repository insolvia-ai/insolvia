import { RequireSession } from '@/components/require-session';
import { Intake } from '@/screens/intake';

/**
 * `/cases/<caseId>/intake` — the structured intake for one case (issue 8.5).
 *
 * The guard wraps the screen here rather than inside `Intake`, matching `/` and
 * `/cases`: "this route needs a session" belongs in `src/app/` where the routes
 * are, and a screen stays a screen.
 */
export default function IntakeRoute() {
  return (
    <RequireSession>
      <Intake />
    </RequireSession>
  );
}
