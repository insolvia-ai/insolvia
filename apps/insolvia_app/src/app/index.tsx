import { RequireFirm } from '@/components/require-firm';
import { RequireSession } from '@/components/require-session';
import { Home } from '@/screens/home';

/**
 * `/` — the app's entry route, and the first protected one.
 *
 * The guards wrap the screen here rather than inside `Home`, so "this route
 * needs a session, and a firm" is visible in `src/app/` where the routes are,
 * and a screen stays a screen. `RequireFirm` joined `RequireSession` here for
 * the dashboard (issue 14.7 / #359): every card on it is firm-owned — cases,
 * tasks, events — so a signed-in person nobody has added to a firm yet has
 * nothing on this page to show, and `RequireFirm` already renders that
 * explanation in place, the same pair `/calendar` and `/my-tasks` use.
 */
export default function HomeRoute() {
  return (
    <RequireSession>
      <RequireFirm>{(membership) => <Home membership={membership} />}</RequireFirm>
    </RequireSession>
  );
}
