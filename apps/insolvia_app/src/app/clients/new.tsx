import { RequireFirm } from '@/components/require-firm';
import { RequireSession } from '@/components/require-session';
import { AddClient } from '@/screens/clients/new';

/**
 * `/clients/new` — "Add client" (ADR 0022 / #354): the person, then
 * optionally straight into their first case. A static segment beside
 * `[clientId]`, which expo-router ranks first, so `new` is never an id.
 */
export default function AddClientRoute() {
  return (
    <RequireSession>
      <RequireFirm>{(membership) => <AddClient membership={membership} />}</RequireFirm>
    </RequireSession>
  );
}
