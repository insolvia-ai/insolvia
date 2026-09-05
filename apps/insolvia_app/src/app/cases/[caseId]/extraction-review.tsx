import { useCase } from '@/components/case-shell';
import { ExtractionReview } from '@/screens/extraction-review';

/**
 * `/cases/<id>/extraction-review` — the review queue that turns extracted
 * candidates into case data (issue #89 / 8.9).
 *
 * The session guard and the `caseId` narrowing both moved up to `_layout.tsx`,
 * which wraps this whole subtree — so the route reads the case the shell
 * already loaded instead of re-deriving it from the URL.
 */
export default function CaseExtractionReviewRoute() {
  const { caseId } = useCase();

  return <ExtractionReview caseId={caseId} />;
}
