import { useLocalSearchParams } from 'expo-router';

import { RequireSession } from '@/components/require-session';
import { FilingPacket } from '@/screens/packet';

/**
 * `/cases/<id>/packet` — assemble and download the Chapter 7 filing packet
 * (issue #96).
 *
 * The same shape as its siblings: the session guard lives here in `src/app/`
 * where the routes are, and the parameter is narrowed to a plain `string` so
 * the screen stays renderable from a test — see `documents.tsx` for the full
 * argument.
 */
export default function CasePacketRoute() {
  const params = useLocalSearchParams();
  const raw = params.caseId;
  const caseId = Array.isArray(raw) ? (raw[0] ?? '') : (raw ?? '');

  return (
    <RequireSession>
      <FilingPacket caseId={caseId} />
    </RequireSession>
  );
}
