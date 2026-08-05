import { RequireFirm } from '@/components/require-firm';
import { RequireSession } from '@/components/require-session';
import { Firm } from '@/screens/firm';

/**
 * `/firm` — the firm's own people, and what each of them may reach.
 *
 * Two guards, and they are two because they answer different questions.
 * `RequireSession` asks "are you signed in"; `RequireFirm` asks "has anybody
 * added you to a firm", which is a state a signed-in person can genuinely be
 * in — self-signup is disabled, so an account exists before a membership does.
 * The screen below assumes both and takes the resolved membership as a prop.
 */
export default function FirmRoute() {
  return (
    <RequireSession>
      <RequireFirm>{(membership) => <Firm membership={membership} />}</RequireFirm>
    </RequireSession>
  );
}
