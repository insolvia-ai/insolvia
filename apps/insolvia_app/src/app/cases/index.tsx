import { RequireSession } from '@/components/require-session';
import { Cases } from '@/screens/cases';

/**
 * `/cases` — the case list and the form that opens one (issue 8.3).
 *
 * The guard wraps the screen here rather than inside `Cases`: "this route
 * needs a session" belongs in `src/app/` where the routes are, and a screen
 * stays a screen. Unlike `/` (the dashboard, issue 14.7 / #359), this route
 * composes no `RequireFirm` — nothing here has ever gated on a firm, and this
 * PR did not add that gate, so a caller in no firm still reaches this screen
 * and its requests answer 403 rather than being explained in advance.
 */
export default function CasesRoute() {
  return (
    <RequireSession>
      <Cases />
    </RequireSession>
  );
}
