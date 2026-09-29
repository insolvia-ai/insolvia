import { useLocalSearchParams } from 'expo-router';

import { RequireSession } from '@/components/require-session';
import { NewCase } from '@/screens/cases/new';

/**
 * `/cases/new` — open a case for one or two of the firm's clients (ADR 0022
 * / #354). `?client=<id>` preselects Debtor 1: the client record's "Start a
 * case for this client" and "Add client"'s "Save and start a case" arrive
 * that way.
 *
 * A static segment beside `[caseId]/`, and expo-router ranks a static match
 * above a dynamic one — so `new` is never read as a case id. The guard is
 * `/cases`' own, for `/cases`' reason (its route file says it).
 */
export default function NewCaseRoute() {
  const { client } = useLocalSearchParams<{ client?: string }>();
  return (
    <RequireSession>
      <NewCase initialClientId={typeof client === 'string' && client !== '' ? client : undefined} />
    </RequireSession>
  );
}
