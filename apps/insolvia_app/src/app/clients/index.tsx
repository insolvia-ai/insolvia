import { RequireFirm } from '@/components/require-firm';
import { RequireSession } from '@/components/require-session';
import { ClientList } from '@/screens/clients';

/**
 * `/clients` — the firm's client directory, the front door (ADR 0022 /
 * #354). The same two guards `/firm` uses: signed in, and in a firm — a
 * directory is the firm's, and the `clients` gate reads the membership
 * `RequireFirm` resolves.
 */
export default function ClientsRoute() {
  return (
    <RequireSession>
      <RequireFirm>{(membership) => <ClientList membership={membership} />}</RequireFirm>
    </RequireSession>
  );
}
