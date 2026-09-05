import { useCase } from '@/components/case-shell';
import { Documents } from '@/screens/documents';

/**
 * `/cases/<id>/documents` — a case's documents (issue 8.6).
 *
 * The session guard and the `caseId` narrowing both moved up to `_layout.tsx`,
 * which wraps this whole subtree — so the route reads the case the shell
 * already loaded instead of re-deriving it from the URL.
 */
export default function CaseDocumentsRoute() {
  const { caseId } = useCase();

  return <Documents caseId={caseId} />;
}
