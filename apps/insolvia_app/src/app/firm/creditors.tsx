import { RequireFirm } from '@/components/require-firm';
import { RequireSession } from '@/components/require-session';
import { CreditorLibrary } from '@/screens/firm/creditors';

/**
 * `/firm/creditors` — the firm's reusable creditor library manager (issue
 * 13.9 / #350).
 *
 * The same two guards `/firm` uses and for the same reasons: `RequireSession`
 * for "are you signed in", `RequireFirm` for "has anybody added you to a
 * firm" — a state a signed-in person can genuinely be in, since self-signup
 * is disabled.
 */
export default function FirmCreditorsRoute() {
  return (
    <RequireSession>
      <RequireFirm>{(membership) => <CreditorLibrary membership={membership} />}</RequireFirm>
    </RequireSession>
  );
}
