import { RequireSession } from '@/components/require-session';
import { Home } from '@/screens/home';

/**
 * `/` — the app's entry route, and the first protected one.
 *
 * The guard wraps the screen here rather than inside `Home`, so "this route
 * needs a session" is visible in `src/app/` where the routes are, and a screen
 * stays a screen.
 */
export default function HomeRoute() {
  return (
    <RequireSession>
      <Home />
    </RequireSession>
  );
}
