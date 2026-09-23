import { useCase } from '@/components/case-shell';
import { FormsHub } from '@/screens/forms-hub';

/**
 * `/cases/<id>/forms` — the forms hub: per-form status, and a single-form PDF
 * preview (issue 13.2 / #343).
 *
 * The session guard and the `caseId` narrowing both live in `_layout.tsx`,
 * which wraps this whole subtree — the same shape `packet.tsx` uses.
 */
export default function CaseFormsRoute() {
  const { caseId } = useCase();

  return <FormsHub caseId={caseId} />;
}
