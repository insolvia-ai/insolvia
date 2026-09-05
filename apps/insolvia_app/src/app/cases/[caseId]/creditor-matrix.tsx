import { useCase } from '@/components/case-shell';
import { CreditorMatrixScreen } from '@/screens/creditor-matrix';

/**
 * `/cases/<id>/creditor-matrix` — generate and save the court's creditor
 * mailing matrix (issue #282).
 *
 * The session guard and the `caseId` narrowing both moved up to `_layout.tsx`,
 * which wraps this whole subtree — so the route reads the case the shell
 * already loaded instead of re-deriving it from the URL.
 */
export default function CaseCreditorMatrixRoute() {
  const { caseId } = useCase();

  return <CreditorMatrixScreen caseId={caseId} />;
}
