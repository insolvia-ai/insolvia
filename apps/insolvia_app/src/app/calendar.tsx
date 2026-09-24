import { RequireFirm } from '@/components/require-firm';
import { RequireSession } from '@/components/require-session';
import { CalendarScreen } from '@/screens/calendar';

/**
 * `/calendar` — every event and deadline the caller may see, by day, week,
 * month or as an agenda (issue 14.6 / #358).
 *
 * The same two guards `/firm` uses and for the same reasons: `RequireSession`
 * for "are you signed in", `RequireFirm` for "has anybody added you to a
 * firm" — the calendar is the firm's, so a person in no firm has none.
 */
export default function CalendarRoute() {
  return (
    <RequireSession>
      <RequireFirm>{(membership) => <CalendarScreen membership={membership} />}</RequireFirm>
    </RequireSession>
  );
}
