import { useLocalSearchParams } from 'expo-router';

import { RequireFirm } from '@/components/require-firm';
import { RequireSession } from '@/components/require-session';
import { ClientRecord } from '@/screens/clients/record';

/**
 * `/clients/<id>` — one client's record and the cases the caller may open
 * for them (ADR 0022 / #354). Keyed on the id so moving from one client to
 * another remounts the screen rather than showing the first one's state.
 */
export default function ClientRecordRoute() {
  const { clientId } = useLocalSearchParams<{ clientId: string }>();
  const id = typeof clientId === 'string' ? clientId : '';
  return (
    <RequireSession>
      <RequireFirm>
        {(membership) => <ClientRecord key={id} membership={membership} clientId={id} />}
      </RequireFirm>
    </RequireSession>
  );
}
