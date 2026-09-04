import { useLocalSearchParams } from 'expo-router';

import { RequireSession } from '@/components/require-session';
import { ExtractionReview } from '@/screens/extraction-review';

/**
 * `/cases/<id>/extraction-review` — the review queue that turns extracted
 * candidates into case data (issue #89 / 8.9).
 *
 * The same shape as its siblings: the session guard lives here in `src/app/`
 * where the routes are, and the parameter is narrowed to a plain `string` so
 * the screen stays renderable from a test — see `documents.tsx` for the full
 * argument. The `extraction_review` PERMISSION gate lives in the screen (and
 * for real, in the API): the route always resolves, and a caller the feature
 * is hidden from is told so instead of 404ing on a page their colleague can
 * link them to.
 */
export default function CaseExtractionReviewRoute() {
  const params = useLocalSearchParams();
  const raw = params.caseId;
  const caseId = Array.isArray(raw) ? (raw[0] ?? '') : (raw ?? '');

  return (
    <RequireSession>
      <ExtractionReview caseId={caseId} />
    </RequireSession>
  );
}
