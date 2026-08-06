import { useLocalSearchParams } from 'expo-router';

import { RequireSession } from '@/components/require-session';
import { Documents } from '@/screens/documents';

/**
 * `/cases/<id>/documents` — a case's documents (issue 8.6).
 *
 * The guard wraps the screen here rather than inside `Documents`, matching
 * `/cases`: "this route needs a session" belongs in `src/app/` where the routes
 * are, and a screen stays a screen.
 *
 * The parameter is read here and passed down as a plain `string`, so the screen
 * has no opinion about how it was routed to and stays renderable from a test or
 * a future nested layout. `useLocalSearchParams` types every value as
 * `string | string[]` — a single-segment route only ever produces the first, but
 * the type is honest about catch-all routes and is narrowed rather than cast.
 */
export default function CaseDocumentsRoute() {
  const params = useLocalSearchParams();
  const raw = params.caseId;
  const caseId = Array.isArray(raw) ? (raw[0] ?? '') : (raw ?? '');

  return (
    <RequireSession>
      <Documents caseId={caseId} />
    </RequireSession>
  );
}
