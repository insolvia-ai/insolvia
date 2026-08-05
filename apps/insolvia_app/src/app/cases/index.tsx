import { RequireSession } from '@/components/require-session';
import { Cases } from '@/screens/cases';

/**
 * `/cases` — the case list and the form that opens one (issue 8.3).
 *
 * The guard wraps the screen here rather than inside `Cases`, matching `/`:
 * "this route needs a session" belongs in `src/app/` where the routes are, and
 * a screen stays a screen.
 */
export default function CasesRoute() {
  return (
    <RequireSession>
      <Cases />
    </RequireSession>
  );
}
