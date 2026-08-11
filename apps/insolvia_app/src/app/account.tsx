import { RequireFirm } from '@/components/require-firm';
import { RequireSession } from '@/components/require-session';
import { Account } from '@/screens/account';

/**
 * `/account` — your own display name and sign-in email.
 *
 * The same two guards as `/firm`, for the same reason: the one editable field
 * lives on the caller's firm-user row, so a signed-in person nobody has added
 * to a firm has nothing here to edit — `RequireFirm` renders that state as the
 * answer it already owns.
 */
export default function AccountRoute() {
  return (
    <RequireSession>
      <RequireFirm>{(membership) => <Account membership={membership} />}</RequireFirm>
    </RequireSession>
  );
}
