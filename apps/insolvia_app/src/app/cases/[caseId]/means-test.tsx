import { useCase } from '@/components/case-shell';
import { MeansTest } from '@/screens/means-test';

/**
 * `/cases/<id>/means-test` — the § 707(b) means test (issue #349): the
 * inputs the test needs, the per-debtor income grid, and the verdict the
 * server recomputes on every save.
 *
 * Same shape as every other case route: the session guard and `caseId`
 * narrowing live in `_layout.tsx`, so this reads the case `CaseShell` already
 * loaded rather than re-deriving it from the URL.
 */
export default function CaseMeansTestRoute() {
  const { caseId } = useCase();

  return <MeansTest caseId={caseId} />;
}
